using Ecommerce.Application.Fiscal;
using Ecommerce.Domain.Fiscal;
using Ecommerce.Domain.Orders;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Ecommerce.Infrastructure.Fiscal;

/// <summary>
/// CNPJ do emitente (banco da plataforma). O PlatformDbContext é pedido ao contêiner só na hora: se aparecesse entre as
/// dependências de um handler, o Wolverine não saberia qual DbContext é o da transação.
/// </summary>
public sealed class TenantIssuer(IServiceProvider services)
{
    public async Task<string> CnpjAsync(Guid tenantId, CancellationToken ct) =>
        (await services.GetRequiredService<PlatformDbContext>().Tenants.AsNoTracking()
            .Where(t => t.Id == tenantId).Select(t => t.Cnpj).SingleAsync(ct)).Value;
}

/// <summary>
/// Monta a NF-e de venda do pedido com os padrões da loja (RF19 CA3) e diz o que impede a emissão. HIPÓTESES (a confirmar
/// com o contador): CFOP da loja conforme a UF do destinatário; consumidor final quando o destinatário tem CPF; venda do
/// site é "pela internet" e do balcão "presencial".
/// </summary>
public static class InvoiceBuilder
{
    public sealed record Prepared(IReadOnlyList<string> Problems, FiscalRecipient? Suggested, FiscalRecipient? Recipient, IReadOnlyList<FiscalInvoiceItem> Items);

    /// <summary>Destinatário a partir do pedido; endereço só existe na entrega (retirada e balcão pedem no painel).</summary>
    public static FiscalRecipient? Suggest(CustomerOrder order)
    {
        var document = order.BuyerCnpj ?? (order.BuyerCpf.Length > 0 ? order.BuyerCpf : null);
        var name = order.BuyerName == CustomerOrder.WalkInCustomer ? string.Empty : order.BuyerName;
        return new FiscalRecipient(name, document ?? string.Empty, order.BuyerEmail.Length > 0 ? order.BuyerEmail : null, order.Address);
    }

    public static async Task<Prepared> PrepareAsync(TenantDbContext db, CustomerOrder order, FiscalSettings settings, FiscalRecipient? input,
        bool providerConnected, bool hasActiveDocument, CancellationToken ct, bool checkRecipient = true)
    {
        var problems = new List<string>();
        if (settings.Mode == FiscalMode.Disabled) problems.Add("A emissão de NF-e está desligada. O Dono liga em Dados da loja → Nota fiscal.");
        else if (settings.Mode == FiscalMode.External) problems.Add("Esta loja emite a NF-e fora da plataforma: informe a chave ou o XML da nota.");
        else
        {
            if (!providerConnected) problems.Add("O provedor fiscal da loja ainda não está conectado.");
            if (settings.State is null || settings.CfopInState is null || settings.CfopOutOfState is null)
                problems.Add("Complete os dados fiscais da loja (UF e CFOP) em Dados da loja → Nota fiscal.");
        }
        if (!InvoiceRules.OrderAccepts(order.Status)) problems.Add("A NF-e de venda só é emitida para pedido pago, enviado, entregue ou retirado.");
        if (hasActiveDocument) problems.Add("Este pedido já tem NF-e autorizada ou em processamento.");

        var ids = order.Items.Select(i => i.PartId).ToList();
        var parts = await db.Parts.AsNoTracking().Where(p => ids.Contains(p.Id))
            .Select(p => new { p.Id, p.Ncm, p.TaxCode, p.FiscalOrigin }).ToDictionaryAsync(p => p.Id, ct);

        FiscalRecipient? recipient = null;
        var suggested = Suggest(order);
        try
        {
            recipient = FiscalRecipient.Normalize(input ?? suggested!);
        }
        catch (ArgumentException e) when (checkRecipient)
        {
            problems.Add(e.Message.Split(" (Parameter", 2)[0]);
        }
        catch (ArgumentException)
        {
            // Rascunho: o painel pede o que falta do destinatário antes de emitir.
        }

        var cfop = recipient?.Address?.State is { } uf && settings.State is { } own
            ? uf == own ? settings.CfopInState : settings.CfopOutOfState
            : settings.CfopInState;
        var items = new List<FiscalInvoiceItem>();
        foreach (var item in order.Items)
        {
            parts.TryGetValue(item.PartId, out var part);
            var ncm = part?.Ncm ?? settings.DefaultNcm;
            var taxCode = part?.TaxCode ?? settings.DefaultTaxCode;
            var origin = part?.FiscalOrigin ?? settings.DefaultOrigin;
            if (ncm is null) problems.Add($"Peça {item.InternalCode} sem NCM (e a loja não tem NCM padrão).");
            if (taxCode is null || origin is null) problems.Add($"Peça {item.InternalCode} sem CSOSN/CST ou origem (e sem padrão na loja).");
            items.Add(new FiscalInvoiceItem(item.InternalCode, item.Title, ncm ?? string.Empty, cfop ?? string.Empty, taxCode ?? string.Empty,
                origin ?? 0, item.Quantity, item.UnitPrice));
        }
        return new Prepared(problems.Distinct().ToList(), suggested, recipient, items);
    }


    public static FiscalInvoice Build(string reference, FiscalSettings settings, string issuerCnpj, CustomerOrder order, Prepared prepared,
        DateTimeOffset now) =>
        new(reference, settings.Environment, new FiscalIssuer(issuerCnpj, settings.State!, settings.Regime), prepared.Recipient!,
            order.Origin == OrderOrigin.Counter ? FiscalPresence.Presential : FiscalPresence.Internet,
            FinalConsumer: prepared.Recipient!.Document.Length == 11, prepared.Items, order.ShippingTotal, order.Total, now);
}
