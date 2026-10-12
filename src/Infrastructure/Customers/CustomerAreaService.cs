using Ecommerce.Application.Customers;
using Ecommerce.Application.Email;
using Ecommerce.Application.Orders;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Customers;
using Ecommerce.Domain.Email;
using Ecommerce.Domain.Orders;
using Ecommerce.Infrastructure.Orders;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Ecommerce.Infrastructure.Customers;

/// <summary>Área do cliente da loja do escopo (RF16, Q39). Só pedidos da loja virtual: balcão não tem e-mail do comprador.</summary>
public sealed class CustomerAreaService(TenantDbContext db, IMessageBus bus, ITenantContext tenant, TimeProvider clock) : ICustomerArea
{
    private const int MaxOrders = 100;

    public async Task RequestLinkAsync(string? email, string origin, CancellationToken ct = default)
    {
        string? normalized;
        try
        {
            normalized = StoreEmailRules.NormalizeAddress(email, "E-mail");
        }
        catch (ArgumentException)
        {
            return;
        }
        if (normalized is null) return;
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Área do cliente sem loja no escopo.");
        await bus.InvokeForTenantAsync(tenantId.ToString(), new IssueCustomerAccessLink(normalized, origin), ct);
    }

    public async Task<StartSessionResult> StartSessionAsync(string? linkToken, CancellationToken ct = default)
    {
        if (CustomerEmail.HashOrNull(linkToken) is not { } hash) return new StartSessionResult(StartSessionOutcome.Invalid);
        var now = clock.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        // Uso único mesmo com dois cliques ao mesmo tempo: só um UPDATE encontra o link ainda sem uso.
        var email = (await db.Database.SqlQuery<string>($"""
            UPDATE customer_access_links SET used_at = {now}
            WHERE token_hash = {hash} AND used_at IS NULL AND expires_at > {now}
            RETURNING email AS "Value"
            """).ToListAsync(ct)).SingleOrDefault();
        if (email is null) return new StartSessionResult(StartSessionOutcome.Invalid);

        var token = CustomerEmail.NewToken();
        var session = new CustomerSession(db.RequireTenantId(), email, CustomerEmail.HashOrNull(token)!, now);
        db.CustomerSessions.Add(session);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new StartSessionResult(StartSessionOutcome.Started, token, email, session.ExpiresAt);
    }

    public async Task<string?> SessionEmailAsync(string? sessionToken, CancellationToken ct = default)
    {
        if (CustomerEmail.HashOrNull(sessionToken) is not { } hash) return null;
        var now = clock.GetUtcNow();
        return await db.CustomerSessions.AsNoTracking()
            .Where(s => s.TokenHash == hash && s.EndedAt == null && s.ExpiresAt > now)
            .Select(s => s.Email).SingleOrDefaultAsync(ct);
    }

    public async Task<IReadOnlyList<CustomerOrderSummary>> OrdersAsync(string email, CancellationToken ct = default) =>
        await Orders(email).OrderByDescending(o => o.PlacedAt).Take(MaxOrders)
            .Select(o => new CustomerOrderSummary(o.Number, o.PlacedAt, o.Status, o.DeliveryMethod, o.Items.Sum(i => i.Quantity), o.Total, o.TrackingCode))
            .ToListAsync(ct);

    public async Task<StoreOrderView?> OrderAsync(string email, long number, CancellationToken ct = default)
    {
        var order = await Orders(email).Include(o => o.Items).SingleOrDefaultAsync(o => o.Number == number, ct);
        return order is null ? null : StorefrontCheckout.ToView(order);
    }

    public async Task EndSessionAsync(string? sessionToken, CancellationToken ct = default)
    {
        if (CustomerEmail.HashOrNull(sessionToken) is not { } hash) return;
        var session = await db.CustomerSessions.SingleOrDefaultAsync(s => s.TokenHash == hash, ct);
        if (session is null) return;
        session.End(clock.GetUtcNow());
        await db.SaveChangesAsync(ct);
    }

    private IQueryable<CustomerOrder> Orders(string email)
    {
        var normalized = CustomerEmail.Normalize(email);
        return db.CustomerOrders.AsNoTracking().Where(o => o.Origin == OrderOrigin.Site && o.BuyerEmail == normalized && normalized != "");
    }
}

/// <summary>Grava o link e agenda o e-mail na mesma transação (RF16).</summary>
public static class CustomerAccessHandler
{
    /// <summary>HIPÓTESE: até 5 links por e-mail por hora em cada loja, contra quem use o formulário para encher a caixa de alguém.</summary>
    public const int MaxLinksPerHour = 5;

    public static async Task<OutgoingMessages> Handle(IssueCustomerAccessLink command, TenantDbContext db, TimeProvider clock, CancellationToken ct)
    {
        var outgoing = new OutgoingMessages();
        var email = CustomerEmail.Normalize(command.Email);
        var now = clock.GetUtcNow();
        var hasOrders = await db.CustomerOrders.AnyAsync(o => o.Origin == OrderOrigin.Site && o.BuyerEmail == email && email != "", ct);
        if (!hasOrders) return outgoing;
        var hourAgo = now.AddHours(-1);
        if (await db.CustomerAccessLinks.CountAsync(l => l.Email == email && l.CreatedAt > hourAgo, ct) >= MaxLinksPerHour) return outgoing;

        var token = CustomerEmail.NewToken();
        db.CustomerAccessLinks.Add(new CustomerAccessLink(db.RequireTenantId(), email, CustomerEmail.HashOrNull(token)!, now));
        outgoing.Add(new SendCustomerAccessLink(email, token, command.Origin));
        return outgoing;
    }
}
