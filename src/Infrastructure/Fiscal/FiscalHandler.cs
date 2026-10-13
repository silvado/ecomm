using System.Globalization;
using System.Xml;
using Ecommerce.Application.Fiscal;
using Ecommerce.Application.Integrations;
using Ecommerce.Application.Storage;
using Ecommerce.Domain.Fiscal;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Ecommerce.Infrastructure.Fiscal;

/// <summary>
/// NF-e de venda (RF19). O pedido de emissão confere tudo na hora (o painel mostra o problema) e grava a nota "processando";
/// o envio ao provedor e as consultas seguem pela fila, com novas tentativas em falha de comunicação (RF28) e cada chamada
/// no log de integração (RF29). Autorizada, XML e DANFE vão para o storage privado da loja.
/// </summary>
public static class FiscalHandler
{
    public const string ActiveDocumentIndex = "ix_fiscal_documents_tenant_id_order_id_active";
    public const int MaxChecks = 40;
    public const int MaxXmlBytes = 1024 * 1024;

    /// <summary>Intervalos da consulta: rápido no começo (a SEFAZ costuma responder em segundos), depois a cada 15 min.</summary>
    public static TimeSpan CheckDelay(int attempt) => attempt switch
    {
        0 => TimeSpan.FromSeconds(3),
        1 => TimeSpan.FromSeconds(10),
        2 => TimeSpan.FromSeconds(30),
        3 => TimeSpan.FromMinutes(1),
        4 => TimeSpan.FromMinutes(5),
        _ => TimeSpan.FromMinutes(15),
    };

    public static async Task<(FiscalCommandResult, OutgoingMessages)> Handle(EmitInvoice command, TenantDbContext db, IFiscalProviderResolver providers, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var order = await db.CustomerOrders.AsNoTracking().Include(o => o.Items).SingleOrDefaultAsync(o => o.Id == command.OrderId, ct);
        if (order is null) return (new FiscalCommandResult(FiscalCommandOutcome.NotFound, "Pedido não encontrado."), outgoing);

        var settings = await FiscalSettingsAsync(db, ct);
        var provider = await providers.ResolveAsync(ct);
        var active = await db.FiscalDocuments.AnyAsync(d => d.OrderId == order.Id
            && (d.Status == FiscalDocumentStatus.Processing || d.Status == FiscalDocumentStatus.Authorized), ct);
        var prepared = await InvoiceBuilder.PrepareAsync(db, order, settings, command.Recipient, provider is not null, active, ct);
        if (prepared.Problems.Count > 0)
            return (new FiscalCommandResult(active || !InvoiceRules.OrderAccepts(order.Status) || settings.Mode != FiscalMode.Provider
                ? FiscalCommandOutcome.NotAllowed : FiscalCommandOutcome.Invalid, string.Join(" ", prepared.Problems)), outgoing);

        // Referência nova a cada tentativa: a rejeitada fica no histórico com a dela.
        var attempt = await db.FiscalDocuments.CountAsync(d => d.OrderId == order.Id, ct) + 1;
        var reference = string.Create(CultureInfo.InvariantCulture, $"PED{order.Number}-{attempt}");
        var document = FiscalDocument.Request(order.TenantId, order.Id, reference, provider!.Name, settings.Environment, prepared.Recipient!,
            command.UserId, clock.GetUtcNow());
        db.FiscalDocuments.Add(document);
        outgoing.Add(new SendInvoice(document.Id));
        return (new FiscalCommandResult(FiscalCommandOutcome.Done, DocumentId: document.Id), outgoing);
    }

    public static async Task<OutgoingMessages> Handle(SendInvoice command, TenantDbContext db, TenantIssuer issuer, IFiscalProviderResolver providers,
        IIntegrationLog log, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var document = await db.FiscalDocuments.SingleOrDefaultAsync(d => d.Id == command.DocumentId, ct);
        if (document is not { Status: FiscalDocumentStatus.Processing, Source: FiscalDocumentSource.Provider }) return outgoing;
        var now = clock.GetUtcNow();
        if (await providers.ResolveAsync(ct) is not { } resolved)
        {
            document.Reject("O provedor fiscal da loja não está mais conectado. Conecte e emita de novo.", now);
            return outgoing;
        }

        var order = await db.CustomerOrders.AsNoTracking().Include(o => o.Items).SingleAsync(o => o.Id == document.OrderId, ct);
        var settings = await FiscalSettingsAsync(db, ct);
        var prepared = await InvoiceBuilder.PrepareAsync(db, order, settings, document.Recipient, providerConnected: true, hasActiveDocument: false, ct);
        var invoice = InvoiceBuilder.Build(document.Reference!, settings, await issuer.CnpjAsync(order.TenantId, ct), order, prepared, now);

        var result = await new LoggedFiscalProvider(resolved, log, order.Id).EmitAsync(invoice, ct);
        outgoing.AddRange(Apply(document, result, attempt: 0, now));
        return outgoing;
    }

    public static async Task<OutgoingMessages> Handle(CheckInvoice command, TenantDbContext db, IFiscalProviderResolver providers, IIntegrationLog log,
        IFileStorage storage, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var document = await db.FiscalDocuments.SingleOrDefaultAsync(d => d.Id == command.DocumentId, ct);
        if (document is not { Status: FiscalDocumentStatus.Processing }) return outgoing;
        if (await providers.ResolveAsync(ct) is not { } resolved) return outgoing;
        var provider = new LoggedFiscalProvider(resolved, log, document.OrderId);
        var now = clock.GetUtcNow();

        var result = await provider.QueryAsync(document.Reference!, ct);
        outgoing.AddRange(Apply(document, result, command.Attempt + 1, now));
        if (document.Status == FiscalDocumentStatus.Authorized)
        {
            // Falha ao baixar volta para a fila inteira (a consulta é repetível): a nota só fica autorizada com os arquivos.
            var xml = await provider.DownloadAsync(document.Reference!, FiscalFile.Xml, ct) ?? throw new FiscalProviderException("XML da NF-e ainda não disponível.");
            var danfe = await provider.DownloadAsync(document.Reference!, FiscalFile.Danfe, ct);
            var xmlKey = $"{document.TenantId}/nfe/{document.Id}.xml";
            await storage.PutAsync(StorageArea.Private, xmlKey, new MemoryStream(xml), "application/xml", ct);
            string? danfeKey = null;
            if (danfe is not null)
            {
                danfeKey = $"{document.TenantId}/nfe/{document.Id}.pdf";
                await storage.PutAsync(StorageArea.Private, danfeKey, new MemoryStream(danfe), "application/pdf", ct);
            }
            document.AttachFiles(xmlKey, danfeKey, now);
        }
        return outgoing;
    }

    /// <summary>Resultado do provedor no documento; ainda processando agenda a próxima consulta (até <see cref="MaxChecks"/>).</summary>
    private static OutgoingMessages Apply(FiscalDocument document, FiscalProviderResult result, int attempt, DateTimeOffset now)
    {
        var outgoing = new OutgoingMessages();
        switch (result.Status)
        {
            case FiscalProviderStatus.Authorized when result.AccessKey is { } key:
                document.Authorize(key, result.Number, result.Series, now);
                break;
            case FiscalProviderStatus.Rejected:
                document.Reject(result.Message ?? "Rejeitada pela SEFAZ.", now);
                break;
            case FiscalProviderStatus.Processing or FiscalProviderStatus.Authorized when attempt < MaxChecks:
                outgoing.Schedule(new CheckInvoice(document.Id, attempt), now + CheckDelay(attempt));
                break;
        }
        return outgoing;
    }

    public static async Task<FiscalCommandResult> Handle(CancelInvoice command, TenantDbContext db, IFiscalProviderResolver providers, IIntegrationLog log,
        TimeProvider clock, CancellationToken ct)
    {
        var document = await db.FiscalDocuments.SingleOrDefaultAsync(d => d.Id == command.DocumentId, ct);
        if (document is null) return new FiscalCommandResult(FiscalCommandOutcome.NotFound, "NF-e não encontrada.");
        var now = clock.GetUtcNow();
        string justification;
        try
        {
            justification = FiscalDocument.NormalizeJustification(command.Justification);
        }
        catch (ArgumentException e)
        {
            return new FiscalCommandResult(FiscalCommandOutcome.Invalid, e.Message.Split(" (Parameter", 2)[0]);
        }
        if (!document.CanCancel(now))
            return new FiscalCommandResult(FiscalCommandOutcome.NotAllowed, document.Status == FiscalDocumentStatus.Authorized
                ? "Passou o prazo de 24 horas para cancelar esta NF-e. Para desfazer a venda, emita uma NF-e de devolução."
                : "Só NF-e autorizada pode ser cancelada.");

        if (document.Source == FiscalDocumentSource.Provider)
        {
            if (await providers.ResolveAsync(ct) is not { } resolved)
                return new FiscalCommandResult(FiscalCommandOutcome.NotAllowed, "O provedor fiscal da loja não está conectado.");
            var result = await new LoggedFiscalProvider(resolved, log, document.OrderId).CancelAsync(document.Reference!, justification, ct);
            if (result.Status != FiscalProviderStatus.Canceled)
                return new FiscalCommandResult(FiscalCommandOutcome.Refused, result.Message ?? "A SEFAZ não aceitou o cancelamento.");
        }
        document.Cancel(justification, now);
        return FiscalCommandResult.Ok;
    }

    public static async Task<FiscalCommandResult> Handle(RegisterExternalInvoice command, TenantDbContext db, TenantIssuer issuer,
        IFileStorage storage, TimeProvider clock, CancellationToken ct)
    {
        var order = await db.CustomerOrders.AsNoTracking().SingleOrDefaultAsync(o => o.Id == command.OrderId, ct);
        if (order is null) return new FiscalCommandResult(FiscalCommandOutcome.NotFound, "Pedido não encontrado.");
        var settings = await FiscalSettingsAsync(db, ct);
        if (settings.Mode != FiscalMode.External)
            return new FiscalCommandResult(FiscalCommandOutcome.NotAllowed, "Esta loja não está no modo de emissão externa (Dados da loja → Nota fiscal).");
        if (!InvoiceRules.OrderAccepts(order.Status))
            return new FiscalCommandResult(FiscalCommandOutcome.NotAllowed, "A NF-e de venda só é registrada para pedido pago, enviado, entregue ou retirado.");
        if (await db.FiscalDocuments.AnyAsync(d => d.OrderId == order.Id && (d.Status == FiscalDocumentStatus.Processing || d.Status == FiscalDocumentStatus.Authorized), ct))
            return new FiscalCommandResult(FiscalCommandOutcome.NotAllowed, "Este pedido já tem NF-e registrada. Remova a atual antes de informar outra.");

        string key;
        try
        {
            var fromXml = command.Xml is { Length: > 0 } xml ? KeyFromXml(xml) : null;
            key = AccessKey.Normalize(command.AccessKey is { Length: > 0 } typed ? typed : fromXml);
            if (fromXml is not null && AccessKey.Normalize(fromXml) != key) throw new ArgumentException("A chave informada não é a do XML anexado.");
        }
        catch (ArgumentException e)
        {
            return new FiscalCommandResult(FiscalCommandOutcome.Invalid, e.Message.Split(" (Parameter", 2)[0]);
        }
        var cnpj = await issuer.CnpjAsync(order.TenantId, ct);
        if (AccessKey.IssuerCnpj(key) != cnpj)
            return new FiscalCommandResult(FiscalCommandOutcome.Invalid, "Esta NF-e não foi emitida pelo CNPJ da loja.");

        var now = clock.GetUtcNow();
        var id = Guid.CreateVersion7();
        string? xmlKey = null;
        if (command.Xml is { Length: > 0 })
        {
            xmlKey = $"{order.TenantId}/nfe/{id}.xml";
            await storage.PutAsync(StorageArea.Private, xmlKey, new MemoryStream(command.Xml), "application/xml", ct);
        }
        var document = FiscalDocument.RegisterExternal(order.TenantId, order.Id, key, xmlKey, command.UserId, now);
        db.FiscalDocuments.Add(document);
        return new FiscalCommandResult(FiscalCommandOutcome.Done, DocumentId: document.Id);
    }

    /// <summary>Chave do XML da NF-e (nfeProc/protNFe/chNFe ou o Id "NFe..." de infNFe), sem resolver entidades externas.</summary>
    public static string KeyFromXml(byte[] xml)
    {
        if (xml.Length > MaxXmlBytes) throw new ArgumentException("XML acima de 1 MB.");
        try
        {
            var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = MaxXmlBytes * 2L };
            using var reader = XmlReader.Create(new MemoryStream(xml), settings);
            string? infNfeId = null;
            while (reader.Read())
            {
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "chNFe") return reader.ReadElementContentAsString();
                if (reader.LocalName == "infNFe" && reader.GetAttribute("Id") is { } attr && attr.StartsWith("NFe", StringComparison.Ordinal)) infNfeId ??= attr[3..];
            }
            return infNfeId ?? throw new ArgumentException("O arquivo não parece ser o XML de uma NF-e.");
        }
        catch (XmlException)
        {
            throw new ArgumentException("O arquivo não é um XML válido.");
        }
    }

    internal static async Task<FiscalSettings> FiscalSettingsAsync(TenantDbContext db, CancellationToken ct) =>
        await db.StoreSettings.AsNoTracking().Select(s => s.Fiscal).SingleOrDefaultAsync(ct) ?? FiscalSettings.Default;
}
