using Ecommerce.Application.Payments;
using Ecommerce.Domain.Billing;

namespace Ecommerce.Application.Billing;

/// <param name="InvoiceId">Referência enviada ao gateway e devolvida na consulta (liga a cobrança à fatura).</param>
/// <param name="ReturnUrl">Para onde o Dono volta depois de pagar (tela Assinatura do painel).</param>
/// <param name="NotificationUrl">Webhook da plataforma para este gateway.</param>
public sealed record SubscriptionChargeRequest(
    Guid InvoiceId, Guid TenantId, string Description, decimal Amount, DateOnly DueDate,
    string PayerName, string PayerDocument, string PayerEmail, string ReturnUrl, string NotificationUrl);

/// <summary>Cobrança criada no gateway: página onde o Dono paga (Pix, boleto ou cartão, conforme o gateway).</summary>
public sealed record SubscriptionCharge(string ExternalId, string PaymentUrl);

public enum ChargeStatus
{
    Pending,
    Paid,
    Canceled,
}

/// <param name="InvoiceReference">A referência enviada na criação (id da fatura).</param>
public sealed record GatewayCharge(string ExternalId, ChargeStatus Status, decimal AmountPaid, string? InvoiceReference);

/// <param name="IdempotencyKey">Igual em reentregas do mesmo aviso.</param>
/// <param name="ChargeId">Cobrança a consultar.</param>
public sealed record BillingNotification(NotificationValidity Validity, string? IdempotencyKey, string? ChargeId)
{
    public static readonly BillingNotification Invalid = new(NotificationValidity.Invalid, null, null);
    public static readonly BillingNotification Other = new(NotificationValidity.Other, null, null);
}

public sealed class SubscriptionBillingException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Gateway da plataforma para cobrar a assinatura das lojas (RF38 CA1, ADR-0008). Qual gateway é está em aberto (Q05);
/// até lá só existe o simulado (dev e testes). Credenciais em variáveis de ambiente (regra 3), nunca em mensagens.
/// </summary>
public interface ISubscriptionBilling
{
    /// <summary>Nome estável usado na URL do webhook e nos registros.</summary>
    string Name { get; }

    Task<SubscriptionCharge> CreateChargeAsync(SubscriptionChargeRequest request, CancellationToken ct = default);

    /// <summary>Cancela a cobrança (fatura cancelada pela equipe) para não ser paga por engano.</summary>
    Task CancelChargeAsync(string externalId, CancellationToken ct = default);

    /// <summary>Confere a assinatura do webhook. Só o id da cobrança é aproveitado do aviso.</summary>
    Task<BillingNotification> ReadNotificationAsync(WebhookRequest request, CancellationToken ct = default);

    /// <summary>Estado verdadeiro da cobrança, consultado no gateway; nulo se não existe.</summary>
    Task<GatewayCharge?> GetChargeAsync(string externalId, CancellationToken ct = default);
}

public sealed record SubscriptionInvoiceView(
    Guid Id, InvoiceKind Kind, string Description, decimal Amount, DateOnly DueDate, InvoiceStatus Status, bool Overdue,
    DateTimeOffset? PaidAt, InvoicePaidVia? PaidVia, string? Note);

/// <param name="MonthlyPrice">Mensalidade do plano atual (zero = sem mensalidade).</param>
/// <param name="StartedOn">Publicação da loja (início da cobrança); nulo enquanto em implantação.</param>
/// <param name="OnlinePayment">Há gateway para pagar pelo painel; sem ele, a equipe registra o pagamento.</param>
public sealed record SubscriptionView(
    string PlanCode, string PlanName, decimal MonthlyPrice, DateOnly? StartedOn, int? DueDay, bool OnlinePayment,
    IReadOnlyList<SubscriptionInvoiceView> Invoices);

public enum PayInvoiceOutcome
{
    Redirect,
    NotFound,
    /// <summary>Paga ou cancelada.</summary>
    NotPayable,
    /// <summary>Sem gateway da plataforma configurado.</summary>
    Unavailable,
}

public sealed record PayInvoiceResult(PayInvoiceOutcome Outcome, string? PaymentUrl);

/// <summary>Tela Assinatura do painel (Dono): plano, mensalidade e faturas da loja do escopo.</summary>
public interface ISubscriptionPanel
{
    Task<SubscriptionView> GetAsync(CancellationToken ct = default);

    /// <summary>Cria (ou reaproveita) a cobrança da fatura no gateway e devolve a página de pagamento.</summary>
    /// <param name="returnUrl">Tela Assinatura no endereço do painel.</param>
    Task<PayInvoiceResult> PayAsync(Guid invoiceId, string returnUrl, CancellationToken ct = default);
}

/// <summary>Recebe o webhook do gateway da plataforma: confere, grava e devolve logo (regra 7).</summary>
public interface ISubscriptionWebhook
{
    Task<WebhookOutcome> ReceiveAsync(string gateway, WebhookRequest request, CancellationToken ct = default);
}

/// <summary>Cobrança de uma loja vista pela equipe da plataforma.</summary>
public sealed record PlatformBillingView(
    Guid TenantId, string PlanName, decimal MonthlyPrice, DateOnly? StartedOn, int? DueDay, IReadOnlyList<SubscriptionInvoiceView> Invoices);

public sealed record PlatformBillingResult(PlatformBillingView? Billing, string? Error, bool NotFound = false);

/// <summary>Cobrança no painel da plataforma (superadmin): taxa de implantação, pagamento manual, cancelamento e preços. Auditado.</summary>
public interface ISubscriptionAdmin
{
    Task<PlatformBillingView?> GetAsync(Guid tenantId, CancellationToken ct = default);

    Task<PlatformBillingResult> AddSetupFeeAsync(Guid tenantId, decimal amount, DateOnly dueDate, CancellationToken ct = default);

    Task<PlatformBillingResult> RecordManualPaymentAsync(Guid tenantId, Guid invoiceId, string? note, CancellationToken ct = default);

    Task<PlatformBillingResult> CancelInvoiceAsync(Guid tenantId, Guid invoiceId, string? note, CancellationToken ct = default);

    /// <summary>Mensalidade do plano; devolve o erro ou nulo.</summary>
    Task<string?> SetPlanPriceAsync(string planCode, decimal price, CancellationToken ct = default);
}

/// <summary>Processa um aviso gravado (fila).</summary>
public sealed record ProcessSubscriptionEvent(Guid EventId);
