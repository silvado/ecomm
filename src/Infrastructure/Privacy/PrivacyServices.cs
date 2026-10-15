using Ecommerce.Application.Common;
using Ecommerce.Application.Privacy;
using Ecommerce.Domain.Fiscal;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Orders;
using Ecommerce.Domain.Platform;
using Ecommerce.Domain.Privacy;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Privacy;

/// <summary>Grava no mesmo <see cref="TenantDbContext"/> de quem chama: a mudança pendente e o registro entram juntos.</summary>
public sealed class PersonalDataAccessLog(TenantDbContext db, PanelActor actor, TimeProvider clock) : IPersonalDataAccessLog
{
    public async Task RecordAsync(string action, Guid? orderId = null, string? details = null, CancellationToken ct = default)
    {
        db.PersonalDataAccesses.Add(new PersonalDataAccess(db.RequireTenantId(), actor.UserId, actor.Email, action, orderId,
            Redaction.Text(details, PersonalDataAccess.DetailsMaxLength), actor.Ip, clock.GetUtcNow()));
        await db.SaveChangesAsync(ct);
    }

    public async Task<PersonalDataAccessPage> ListAsync(PersonalDataAccessFilter filter, CancellationToken ct = default)
    {
        var page = Math.Max(1, filter.Page);
        var size = Math.Clamp(filter.PageSize, 1, 200);
        var query = db.PersonalDataAccesses.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(filter.Action)) query = query.Where(a => a.Action == filter.Action);
        if (filter.OrderId is { } orderId) query = query.Where(a => a.OrderId == orderId);

        var rows = await (from a in query
                          join o in db.CustomerOrders on a.OrderId equals o.Id into orders
                          from o in orders.DefaultIfEmpty()
                          orderby a.At descending, a.Id descending
                          select new PersonalDataAccessItem(a.At, a.UserEmail, a.Action, a.OrderId, o == null ? null : o.Number, a.Details, a.Ip))
            .Skip((page - 1) * size).Take(size + 1).ToListAsync(ct);
        return new PersonalDataAccessPage(rows.Take(size).ToList(), page, size, rows.Count > size);
    }
}

/// <summary>Anonimização do comprador (RNF04): tudo ou nada, numa transação, com o registro de acesso junto.</summary>
public sealed class BuyerAnonymization(TenantDbContext db, IPersonalDataAccessLog log, TimeProvider clock) : IBuyerAnonymization
{
    public async Task<(AnonymizationPreview? Preview, string? Error)> PreviewAsync(DataSubject subject, CancellationToken ct = default)
    {
        if (Match(subject) is not { } match) return (null, InvalidSubject);
        var orders = await db.CustomerOrders.AsNoTracking().Where(match).Select(o => new { o.Id, o.Number, o.Status, o.AnonymizedAt }).ToListAsync(ct);
        var ids = orders.Select(o => o.Id).ToList();
        var invoiced = await InvoicedAsync(ids, ct);
        return (new AnonymizationPreview(orders.Count,
            orders.Where(o => IsInProgress(o.Status)).Select(o => o.Number).Order().ToList(),
            invoiced.Count, orders.Count(o => o.AnonymizedAt is not null)), null);
    }

    public async Task<AnonymizationResult> AnonymizeAsync(DataSubject subject, CancellationToken ct = default)
    {
        if (Match(subject) is not { } match) return new AnonymizationResult(null, InvalidSubject);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Trava os pedidos do titular: um pagamento ou envio no meio do caminho não passa batido.
        var orders = await db.CustomerOrders.Where(match).ToListAsync(ct);
        if (orders.Count == 0) return new AnonymizationResult(null, "Nenhum pedido com este e-mail ou documento.");
        await db.Database.ExecuteSqlAsync($"SELECT 1 FROM customer_orders WHERE id = ANY({orders.Select(o => o.Id).ToArray()}) FOR UPDATE", ct);
        foreach (var order in orders) await db.Entry(order).ReloadAsync(ct);

        var inProgress = orders.Where(o => o.IsInProgress).Select(o => o.Number).Order().ToList();
        if (inProgress.Count > 0)
            return new AnonymizationResult(null,
                $"Há pedidos em andamento ({string.Join(", ", inProgress.Select(n => $"nº {n}"))}). Conclua ou cancele antes de anonimizar.");

        // Acesso à área "Meus pedidos" pelo e-mail (o informado e os dos pedidos): links e sessões saem junto.
        var emails = orders.Select(o => o.BuyerEmail.ToLowerInvariant()).Append(NormalizedEmail(subject.Email) ?? string.Empty)
            .Where(e => e.Length > 0).ToHashSet();

        var invoiced = await InvoicedAsync(orders.Select(o => o.Id).ToList(), ct);
        var already = orders.Count(o => o.AnonymizedAt is not null);
        var now = clock.GetUtcNow();
        foreach (var order in orders) order.AnonymizeBuyer(invoiced.Contains(order.Id), now);

        await db.CustomerAccessLinks.Where(l => emails.Contains(l.Email)).ExecuteDeleteAsync(ct);
        await db.CustomerSessions.Where(s => emails.Contains(s.Email)).ExecuteDeleteAsync(ct);

        await log.RecordAsync(PersonalDataActions.BuyerAnonymized, details:
            $"{orders.Count} pedido(s); {invoiced.Count} com nota fiscal mantêm nome e CPF/CNPJ", ct: ct);
        await transaction.CommitAsync(ct);
        return new AnonymizationResult(new AnonymizationPreview(orders.Count, [], invoiced.Count, already), null);
    }

    private const string InvalidSubject = "Informe o e-mail ou o CPF/CNPJ que o comprador usou nos pedidos.";

    private static bool IsInProgress(OrderStatus status) =>
        status is OrderStatus.PendingPayment or OrderStatus.Paid or OrderStatus.NeedsReview or OrderStatus.Shipped;

    /// <summary>Notas que a lei manda guardar: autorizadas ou canceladas (emitidas aqui ou registradas de fora).</summary>
    private async Task<HashSet<Guid>> InvoicedAsync(List<Guid> orderIds, CancellationToken ct) =>
        (await db.FiscalDocuments.AsNoTracking()
            .Where(f => orderIds.Contains(f.OrderId) && (f.Status == FiscalDocumentStatus.Authorized || f.Status == FiscalDocumentStatus.Canceled))
            .Select(f => f.OrderId).Distinct().ToListAsync(ct)).ToHashSet();

    private static System.Linq.Expressions.Expression<Func<CustomerOrder, bool>>? Match(DataSubject subject)
    {
        var email = NormalizedEmail(subject.Email);
        var document = new string((subject.Document ?? string.Empty).Where(char.IsAsciiLetterOrDigit).ToArray()).ToUpperInvariant();
        var cpf = document.Length == 11 && document.All(char.IsAsciiDigit) ? document : null;
        var cnpj = document.Length == 14 && Cnpj.TryParse(document, out _) ? document : null;
        if (email is null && cpf is null && cnpj is null) return null;
        // E-mail gravado sempre em minúsculas (NormalizeBuyer). Só pelo que foi informado; CPF e CNPJ vazios (já anonimizados ou balcão sem documento) nunca casam.
        return o => (email != null && o.BuyerEmail == email)
            || (cpf != null && o.BuyerCpf == cpf)
            || (cnpj != null && o.BuyerCnpj == cnpj);
    }

    private static string? NormalizedEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return null;
        try { return UserAccount.NormalizeEmail(email); }
        catch (ArgumentException) { return null; }
    }
}
