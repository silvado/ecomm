namespace Ecommerce.Domain.Billing;

/// <summary>
/// Assinatura da loja com a plataforma (RF38). Decisão do usuário (16/10/2026): a mensalidade começa quando o Dono publica a
/// loja e vence todo mês no dia da publicação (até o dia 28, para existir em todos os meses). Vive no banco <c>plataforma</c>.
/// </summary>
public sealed class Subscription
{
    public const int MaxDueDay = 28;

    private Subscription() { }

    public Guid TenantId { get; private set; }

    /// <summary>Data (America/Sao_Paulo) da publicação: vencimento da 1ª mensalidade.</summary>
    public DateOnly StartedOn { get; private set; }

    /// <summary>Dia do mês em que as mensalidades seguintes vencem.</summary>
    public int DueDay { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static Subscription Start(Guid tenantId, DateOnly startedOn, DateTimeOffset now) => new()
    {
        TenantId = tenantId,
        StartedOn = startedOn,
        DueDay = Math.Min(startedOn.Day, MaxDueDay),
        CreatedAt = now,
    };

    /// <summary>
    /// Mensalidades que já devem existir até <paramref name="until"/> (o job gera com antecedência): competência (1º dia do
    /// mês) e vencimento. A 1ª vence no dia da publicação; as outras, no <see cref="DueDay"/> dos meses seguintes.
    /// </summary>
    public IEnumerable<(DateOnly Period, DateOnly DueDate)> PeriodsUntil(DateOnly until)
    {
        var period = new DateOnly(StartedOn.Year, StartedOn.Month, 1);
        var due = StartedOn;
        while (due <= until)
        {
            yield return (period, due);
            period = period.AddMonths(1);
            due = new DateOnly(period.Year, period.Month, DueDay);
        }
    }
}

public enum InvoiceKind
{
    /// <summary>Taxa de implantação (lançada pela equipe da plataforma).</summary>
    Setup,
    Monthly,
}

public enum InvoiceStatus
{
    Open,
    Paid,
    Canceled,
}

public enum InvoicePaidVia
{
    /// <summary>Confirmado pelo gateway da plataforma (webhook + consulta).</summary>
    Gateway,
    /// <summary>Registrado pela equipe da plataforma (Pix, transferência).</summary>
    Manual,
}

/// <summary>Fatura da assinatura (mensalidade ou taxa de implantação) — RF38 CA1.</summary>
public sealed class SubscriptionInvoice
{
    public const int DescriptionMaxLength = 120;
    public const int NoteMaxLength = 300;
    public const int ChargeIdMaxLength = 200;
    public const int PaymentUrlMaxLength = 1000;
    public const decimal MaxAmount = 9_999_999_999.99m; // numeric(12,2)

    private SubscriptionInvoice() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public InvoiceKind Kind { get; private set; }

    /// <summary>Competência (1º dia do mês) da mensalidade; nulo na taxa de implantação.</summary>
    public DateOnly? Period { get; private set; }

    public string Description { get; private set; } = string.Empty;
    public decimal Amount { get; private set; }
    public DateOnly DueDate { get; private set; }
    public InvoiceStatus Status { get; private set; }

    /// <summary>Cobrança no gateway da plataforma, criada quando o Dono clica em Pagar.</summary>
    public string? ChargeId { get; private set; }

    public string? PaymentUrl { get; private set; }
    public DateTimeOffset? PaidAt { get; private set; }
    public InvoicePaidVia? PaidVia { get; private set; }
    public decimal? PaidAmount { get; private set; }

    /// <summary>Observação da equipe (pagamento manual ou cancelamento).</summary>
    public string? Note { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static SubscriptionInvoice Monthly(Guid tenantId, DateOnly period, DateOnly dueDate, string planName, decimal amount, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planName);
        if (period.Day != 1) throw new ArgumentException("A competência é o 1º dia do mês.", nameof(period));
        var description = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Mensalidade {period:MM'/'yyyy} — plano {planName.Trim()}");
        return Create(tenantId, InvoiceKind.Monthly, period, description, amount, dueDate, now);
    }

    public static SubscriptionInvoice Setup(Guid tenantId, decimal amount, DateOnly dueDate, DateTimeOffset now) =>
        Create(tenantId, InvoiceKind.Setup, null, "Taxa de implantação", amount, dueDate, now);

    private static SubscriptionInvoice Create(Guid tenantId, InvoiceKind kind, DateOnly? period, string description, decimal amount, DateOnly dueDate,
        DateTimeOffset now)
    {
        if (!IsValidAmount(amount)) throw new ArgumentException("Valor inválido.", nameof(amount));
        return new SubscriptionInvoice
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            Kind = kind,
            Period = period,
            Description = description.Length > DescriptionMaxLength ? description[..DescriptionMaxLength] : description,
            Amount = amount,
            DueDate = dueDate,
            Status = InvoiceStatus.Open,
            CreatedAt = now,
        };
    }

    /// <summary>Positivo, em centavos e dentro de numeric(12,2).</summary>
    public static bool IsValidAmount(decimal amount) => amount > 0 && amount <= MaxAmount && decimal.Round(amount, 2) == amount;

    /// <summary>Vencida e não paga: o que conta para a régua de inadimplência (RF38 CA2).</summary>
    public bool IsOverdue(DateOnly today) => Status == InvoiceStatus.Open && DueDate < today;

    public void AttachCharge(string chargeId, string paymentUrl)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(chargeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(paymentUrl);
        if (Status != InvoiceStatus.Open) throw new InvalidOperationException("Só uma fatura em aberto recebe cobrança.");
        if (chargeId.Length > ChargeIdMaxLength || paymentUrl.Length > PaymentUrlMaxLength) throw new ArgumentException("Cobrança inválida.", nameof(chargeId));
        ChargeId = chargeId;
        PaymentUrl = paymentUrl;
    }

    /// <summary>
    /// Registra o pagamento; devolve false se já estava paga (reentrega do aviso). Paga depois de cancelada também conta:
    /// o dinheiro entrou (HIPÓTESE; a equipe devolve ou abate fora do sistema).
    /// </summary>
    public bool MarkPaid(decimal amount, InvoicePaidVia via, string? note, DateTimeOffset now)
    {
        if (Status == InvoiceStatus.Paid) return false;
        Status = InvoiceStatus.Paid;
        PaidAt = now;
        PaidVia = via;
        PaidAmount = amount;
        if (!string.IsNullOrWhiteSpace(note)) Note = Trim(note);
        return true;
    }

    public void Cancel(string? note)
    {
        if (Status != InvoiceStatus.Open) throw new InvalidOperationException("Só uma fatura em aberto pode ser cancelada.");
        Status = InvoiceStatus.Canceled;
        if (!string.IsNullOrWhiteSpace(note)) Note = Trim(note);
    }

    private static string Trim(string note)
    {
        var value = note.Trim();
        return value.Length > NoteMaxLength ? value[..NoteMaxLength] : value;
    }
}
