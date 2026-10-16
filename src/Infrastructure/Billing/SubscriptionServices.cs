using Ecommerce.Application.Billing;
using Ecommerce.Application.Payments;
using Ecommerce.Application.Platform;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Billing;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine;

namespace Ecommerce.Infrastructure.Billing;

/// <summary>Tela Assinatura do painel (RF38): o Dono vê as faturas da loja e paga pelo gateway da plataforma.</summary>
public sealed class SubscriptionPanel(PlatformDbContext db, ITenantContext tenant, IEnumerable<ISubscriptionBilling> gateways, TimeProvider clock)
    : ISubscriptionPanel
{
    /// <summary>Faturas mostradas (as mais recentes).</summary>
    public const int InvoiceLimit = 24;

    public async Task<SubscriptionView> GetAsync(CancellationToken ct = default)
    {
        var tenantId = TenantId();
        var plan = await (from t in db.Tenants
                          join p in db.Plans on t.PlanCode equals p.Code
                          where t.Id == tenantId
                          select new { p.Code, p.Name, p.MonthlyPrice }).SingleAsync(ct);
        var subscription = await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var today = BillingCalendar.Today(clock);
        var invoices = await db.SubscriptionInvoices.AsNoTracking().Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.DueDate).ThenByDescending(i => i.Id).Take(InvoiceLimit).ToListAsync(ct);
        return new SubscriptionView(plan.Code, plan.Name, plan.MonthlyPrice, subscription?.StartedOn, subscription?.DueDay, gateways.Any(),
            invoices.Select(i => BillingCalendar.View(i, today)).ToList());
    }

    public async Task<BillingStandingView> StandingAsync(CancellationToken ct = default)
    {
        var tenantId = TenantId();
        var today = BillingCalendar.Today(clock);
        var status = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => t.Status).SingleAsync(ct);
        var oldest = await db.SubscriptionInvoices.AsNoTracking()
            .Where(i => i.TenantId == tenantId && i.Status == InvoiceStatus.Open && i.DueDate < today)
            .MinAsync(i => (DateOnly?)i.DueDate, ct);
        return new BillingStandingView(status, DunningPolicy.StageFor(oldest, today), oldest, Tenant.IsPanelReadOnly(status));
    }

    public async Task<PayInvoiceResult> PayAsync(Guid invoiceId, string returnUrl, CancellationToken ct = default)
    {
        var tenantId = TenantId();
        var invoice = await db.SubscriptionInvoices.SingleOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId, ct);
        if (invoice is null) return new PayInvoiceResult(PayInvoiceOutcome.NotFound, null);
        if (invoice.Status != InvoiceStatus.Open) return new PayInvoiceResult(PayInvoiceOutcome.NotPayable, null);
        if (invoice.PaymentUrl is { } existing) return new PayInvoiceResult(PayInvoiceOutcome.Redirect, existing);
        if (gateways.FirstOrDefault() is not { } gateway) return new PayInvoiceResult(PayInvoiceOutcome.Unavailable, null);

        var payer = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => new { t.LegalName, t.Cnpj }).SingleAsync(ct);
        var ownerEmail = await (from m in db.TenantMemberships
                                join u in db.UserAccounts on m.UserId equals u.Id
                                where m.TenantId == tenantId && m.Role == TenantRole.Owner
                                orderby m.CreatedAt
                                select u.Email).FirstAsync(ct);
        var origin = new Uri(returnUrl).GetLeftPart(UriPartial.Authority);
        var charge = await gateway.CreateChargeAsync(new SubscriptionChargeRequest(invoice.Id, tenantId, invoice.Description, invoice.Amount, invoice.DueDate,
            payer.LegalName, payer.Cnpj.Value, ownerEmail, returnUrl, $"{origin}/api/webhooks/assinatura/{gateway.Name}"), ct);

        // Dois cliques ao mesmo tempo: só a primeira cobrança fica; a outra é cancelada no gateway (nunca duas para pagar).
        var attached = await db.SubscriptionInvoices
            .Where(i => i.Id == invoice.Id && i.ChargeId == null && i.Status == InvoiceStatus.Open)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.ChargeId, charge.ExternalId).SetProperty(i => i.PaymentUrl, charge.PaymentUrl), ct);
        if (attached == 1) return new PayInvoiceResult(PayInvoiceOutcome.Redirect, charge.PaymentUrl);

        await gateway.CancelChargeAsync(charge.ExternalId, ct);
        var current = await db.SubscriptionInvoices.AsNoTracking().Where(i => i.Id == invoice.Id).Select(i => new { i.Status, i.PaymentUrl }).SingleAsync(ct);
        return current is { Status: InvoiceStatus.Open, PaymentUrl: { } url }
            ? new PayInvoiceResult(PayInvoiceOutcome.Redirect, url)
            : new PayInvoiceResult(PayInvoiceOutcome.NotPayable, null);
    }

    private Guid TenantId() => tenant.TenantId ?? throw new InvalidOperationException("Assinatura sem loja no escopo.");
}

/// <summary>Cobrança das lojas no painel da plataforma (superadmin, ADR-0007): cada ação entra na auditoria.</summary>
public sealed class SubscriptionAdmin(PlatformDbContext db, IPlatformAudit audit, IEnumerable<ISubscriptionBilling> gateways, ITenantCatalog catalog,
    IMemoryCache cache, IMessageBus bus, TimeProvider clock, ILogger<SubscriptionAdmin> logger) : ISubscriptionAdmin
{
    public async Task<PlatformBillingView?> GetAsync(Guid tenantId, CancellationToken ct = default)
    {
        var plan = await (from t in db.Tenants
                          join p in db.Plans on t.PlanCode equals p.Code
                          where t.Id == tenantId
                          select new { p.Name, p.MonthlyPrice }).SingleOrDefaultAsync(ct);
        if (plan is null) return null;
        var subscription = await db.Subscriptions.AsNoTracking().SingleOrDefaultAsync(s => s.TenantId == tenantId, ct);
        var today = BillingCalendar.Today(clock);
        var invoices = await db.SubscriptionInvoices.AsNoTracking().Where(i => i.TenantId == tenantId)
            .OrderByDescending(i => i.DueDate).ThenByDescending(i => i.Id).Take(SubscriptionPanel.InvoiceLimit).ToListAsync(ct);
        return new PlatformBillingView(tenantId, plan.Name, plan.MonthlyPrice, subscription?.StartedOn, subscription?.DueDay,
            invoices.Select(i => BillingCalendar.View(i, today)).ToList());
    }

    public async Task<PlatformBillingResult> AddSetupFeeAsync(Guid tenantId, decimal amount, DateOnly dueDate, CancellationToken ct = default)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, ct)) return NotFound();
        if (!SubscriptionInvoice.IsValidAmount(amount)) return Fail("Informe um valor maior que zero, com até 2 casas decimais.");
        if (dueDate < BillingCalendar.Today(clock)) return Fail("O vencimento não pode ser no passado.");
        if (await db.SubscriptionInvoices.AnyAsync(i => i.TenantId == tenantId && i.Kind == InvoiceKind.Setup && i.Status != InvoiceStatus.Canceled, ct))
            return Fail("A loja já tem taxa de implantação lançada. Cancele a atual para lançar outra.");
        db.SubscriptionInvoices.Add(SubscriptionInvoice.Setup(tenantId, amount, dueDate, clock.GetUtcNow()));
        await audit.RecordAsync(PlatformAuditActions.SetupFeeAdded, tenantId, string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"R$ {amount:0.00}, vence {dueDate:dd'/'MM'/'yyyy}"), ct);
        return new PlatformBillingResult(await GetAsync(tenantId, ct), null);
    }

    public Task<PlatformBillingResult> RecordManualPaymentAsync(Guid tenantId, Guid invoiceId, string? note, CancellationToken ct = default) =>
        ChangeAsync(tenantId, invoiceId, async invoice =>
        {
            if (invoice.Status != InvoiceStatus.Open) return "Só uma fatura em aberto pode ser marcada como paga.";
            if (string.IsNullOrWhiteSpace(note)) return "Informe como foi pago (ex.: Pix em 10/11).";
            invoice.MarkPaid(invoice.Amount, InvoicePaidVia.Manual, note, clock.GetUtcNow());
            if (invoice.ChargeId is { } charge && gateways.FirstOrDefault() is { } gateway) await gateway.CancelChargeAsync(charge, ct);
            await audit.RecordAsync(PlatformAuditActions.InvoicePaidManually, tenantId, $"{invoice.Description}: {note}", ct);
            return null;
        }, ct);

    public Task<PlatformBillingResult> CancelInvoiceAsync(Guid tenantId, Guid invoiceId, string? note, CancellationToken ct = default) =>
        ChangeAsync(tenantId, invoiceId, async invoice =>
        {
            if (invoice.Status != InvoiceStatus.Open) return "Só uma fatura em aberto pode ser cancelada.";
            if (string.IsNullOrWhiteSpace(note)) return "Informe o motivo do cancelamento.";
            if (invoice.ChargeId is { } charge && gateways.FirstOrDefault() is { } gateway) await gateway.CancelChargeAsync(charge, ct);
            invoice.Cancel(note);
            await audit.RecordAsync(PlatformAuditActions.InvoiceCanceled, tenantId, $"{invoice.Description}: {note}", ct);
            return null;
        }, ct);

    public async Task<string?> SetPlanPriceAsync(string planCode, decimal price, CancellationToken ct = default)
    {
        var code = planCode?.Trim().ToLowerInvariant() ?? string.Empty;
        var plan = await db.Plans.SingleOrDefaultAsync(p => p.Code == code, ct);
        if (plan is null) return "Plano inexistente.";
        if (price < 0 || price > SubscriptionInvoice.MaxAmount || decimal.Round(price, 2) != price) return "Preço inválido: use até 2 casas decimais.";
        if (plan.MonthlyPrice == price) return null;
        var previous = plan.MonthlyPrice;
        plan.SetMonthlyPrice(price);
        await audit.RecordAsync(PlatformAuditActions.PlanPriceChanged, null, string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{code}: R$ {previous:0.00} → R$ {price:0.00}"), ct);
        return null;
    }

    private async Task<PlatformBillingResult> ChangeAsync(Guid tenantId, Guid invoiceId, Func<SubscriptionInvoice, Task<string?>> change, CancellationToken ct)
    {
        var invoice = await db.SubscriptionInvoices.SingleOrDefaultAsync(i => i.Id == invoiceId && i.TenantId == tenantId, ct);
        if (invoice is null) return NotFound();
        if (await change(invoice) is { } error) return Fail(error);
        // Pago ou cancelado o que estava vencido, a régua desfaz o bloqueio na hora (RF38 CA3).
        var changes = await BillingStanding.ApplyAsync(db, [tenantId], BillingCalendar.Today(clock), ct);
        await db.SaveChangesAsync(ct);
        await BillingStanding.AnnounceAsync(changes, catalog, cache, bus, logger);
        return new PlatformBillingResult(await GetAsync(tenantId, ct), null);
    }

    private static PlatformBillingResult Fail(string error) => new(null, error);

    private static PlatformBillingResult NotFound() => new(null, "Loja ou fatura não encontrada.", NotFound: true);
}

/// <summary>
/// Webhook do gateway da plataforma (RF38, regra 7): confere a assinatura, grava o aviso com chave de idempotência
/// (reentrega não duplica) e põe na fila. O que o aviso diz não é confiado: o processamento consulta o gateway.
/// </summary>
public sealed class SubscriptionWebhook(PlatformDbContext db, IEnumerable<ISubscriptionBilling> gateways, IMessageBus bus, TimeProvider clock)
    : ISubscriptionWebhook
{
    public async Task<WebhookOutcome> ReceiveAsync(string gateway, WebhookRequest request, CancellationToken ct = default)
    {
        if (gateways.FirstOrDefault(g => g.Name == gateway) is not { } resolved) return WebhookOutcome.NotFound;
        var notification = await resolved.ReadNotificationAsync(request, ct);
        if (notification.Validity == NotificationValidity.Other) return WebhookOutcome.Ignored;
        if (notification is not { Validity: NotificationValidity.Payment, IdempotencyKey: { Length: > 0 and <= SubscriptionEvent.KeyMaxLength } key,
                ChargeId: { Length: > 0 and <= SubscriptionInvoice.ChargeIdMaxLength } chargeId })
            return WebhookOutcome.Rejected;

        var received = new SubscriptionEvent(resolved.Name, key, chargeId, clock.GetUtcNow());
        var inserted = await db.Database.SqlQuery<Guid>($"""
            INSERT INTO subscription_events (id, gateway, idempotency_key, charge_id, status, received_at)
            VALUES ({received.Id}, {received.Gateway}, {received.IdempotencyKey}, {received.ChargeId}, {nameof(SubscriptionEventStatus.Received)},
                    {received.ReceivedAt})
            ON CONFLICT (gateway, idempotency_key) DO NOTHING
            RETURNING id AS "Value"
            """).ToListAsync(ct);
        if (inserted.Count == 0) return WebhookOutcome.Ignored;
        // Fora de transação com o banco plataforma: se cair entre gravar e enfileirar, o job reenfileira os avisos parados.
        await bus.PublishAsync(new ProcessSubscriptionEvent(received.Id));
        return WebhookOutcome.Accepted;
    }
}

/// <summary>
/// Baixa da fatura pelo aviso do gateway (fila). O PlatformDbContext é pedido ao contêiner só na hora: entre as dependências
/// de um handler, o Wolverine não saberia qual DbContext é o da transação.
/// </summary>
public sealed partial class SubscriptionPayments(IServiceProvider services, TimeProvider clock, ILogger<SubscriptionPayments> logger)
{
    public async Task ProcessAsync(Guid eventId, CancellationToken ct)
    {
        var db = services.GetRequiredService<PlatformDbContext>();
        var received = await db.SubscriptionEvents.SingleOrDefaultAsync(e => e.Id == eventId, ct);
        if (received is not { Status: SubscriptionEventStatus.Received }) return;
        var now = clock.GetUtcNow();

        var gateway = services.GetServices<ISubscriptionBilling>().FirstOrDefault(g => g.Name == received.Gateway);
        var charge = gateway is null ? null : await gateway.GetChargeAsync(received.ChargeId, ct);
        if (charge is not { Status: ChargeStatus.Paid })
        {
            received.Finish(SubscriptionEventStatus.Ignored, now);
            await db.SaveChangesAsync(ct);
            return;
        }

        var invoice = Guid.TryParse(charge.InvoiceReference, out var invoiceId)
            ? await db.SubscriptionInvoices.SingleOrDefaultAsync(i => i.Id == invoiceId, ct)
            : null;
        if (invoice is null)
        {
            LogPaymentWithoutInvoice(logger, received.Gateway, charge.ExternalId);
            received.Finish(SubscriptionEventStatus.Ignored, now);
        }
        // HIPÓTESE: pago a menor não quita; a equipe confere e registra à mão se for o caso.
        else if (charge.AmountPaid < invoice.Amount)
        {
            LogUnderpaid(logger, invoice.TenantId, invoice.Id, charge.AmountPaid, invoice.Amount);
            received.Finish(SubscriptionEventStatus.Ignored, now);
        }
        else
        {
            if (invoice.MarkPaid(charge.AmountPaid, InvoicePaidVia.Gateway, null, now)) LogPaid(logger, invoice.TenantId, invoice.Id);
            received.Finish(SubscriptionEventStatus.Processed, now);
        }
        await db.SaveChangesAsync(ct);

        // RF38 CA3: pagou, a loja volta na hora (se não sobrou fatura vencida e o bloqueio foi da régua).
        if (invoice is { Status: InvoiceStatus.Paid })
        {
            var changes = await BillingStanding.ApplyAsync(db, [invoice.TenantId], BillingCalendar.Today(clock), ct);
            await db.SaveChangesAsync(ct);
            await BillingStanding.AnnounceAsync(changes, services.GetRequiredService<ITenantCatalog>(), services.GetRequiredService<IMemoryCache>(),
                services.GetRequiredService<IMessageBus>(), logger);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Fatura da assinatura paga: loja {TenantId}, fatura {InvoiceId}")]
    private static partial void LogPaid(ILogger logger, Guid tenantId, Guid invoiceId);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Pagamento da assinatura sem fatura: gateway {Gateway}, cobrança {ChargeId}")]
    private static partial void LogPaymentWithoutInvoice(ILogger logger, string gateway, string chargeId);

    [LoggerMessage(Level = LogLevel.Critical, Message = "Fatura da assinatura paga a menor: loja {TenantId}, fatura {InvoiceId}, pago {Paid}, devido {Due}")]
    private static partial void LogUnderpaid(ILogger logger, Guid tenantId, Guid invoiceId, decimal paid, decimal due);
}

/// <summary>Handler do Wolverine para os avisos do gateway da plataforma (sem loja no envelope).</summary>
public static class SubscriptionPaymentHandler
{
    public static Task Handle(ProcessSubscriptionEvent command, SubscriptionPayments payments, CancellationToken ct) =>
        payments.ProcessAsync(command.EventId, ct);
}
