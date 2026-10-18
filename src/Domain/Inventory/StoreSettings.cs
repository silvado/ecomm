using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Inventory;

/// <summary>
/// Configurações operacionais da loja: prazo de reserva (RF12 CA2), peças sem estoque na vitrine (RF13 CA4) e frete
/// (RF14: CEP de origem e retirada no balcão).
/// </summary>
public sealed class StoreSettings : ITenantOwned
{
    public const int DefaultReservationMinutes = 30;

    private StoreSettings() { }

    public StoreSettings(Guid tenantId, int reservationMinutes = DefaultReservationMinutes)
    {
        TenantId = tenantId;
        SetReservationMinutes(reservationMinutes);
    }

    public Guid TenantId { get; private set; }
    public int ReservationMinutes { get; private set; }

    /// <summary>RF13 CA4: peça sem estoque some da vitrine (verdadeiro) ou aparece como indisponível (padrão).</summary>
    public bool HideOutOfStock { get; private set; }

    public void SetHideOutOfStock(bool hide) => HideOutOfStock = hide;

    public const int PickupAddressMaxLength = 300;

    /// <summary>CEP de onde saem as entregas (RF14 CA1). Sem ele, a loja só oferece retirada.</summary>
    public string? OriginPostalCode { get; private set; }

    /// <summary>Retirada no balcão (RF14 CA2).</summary>
    public bool PickupEnabled { get; private set; }

    /// <summary>Endereço e horário mostrados ao comprador que escolhe retirar.</summary>
    public string? PickupAddress { get; private set; }

    public void ConfigureShipping(string? originPostalCode, bool pickupEnabled, string? pickupAddress)
    {
        var origin = string.IsNullOrWhiteSpace(originPostalCode) ? null : Shipping.PostalCode.Parse(originPostalCode).Value;
        var address = string.IsNullOrWhiteSpace(pickupAddress) ? null : pickupAddress.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (address is { Length: > PickupAddressMaxLength }) throw new ArgumentException($"Endereço de retirada acima de {PickupAddressMaxLength} caracteres.", nameof(pickupAddress));
        if (pickupEnabled && address is null) throw new ArgumentException("Informe o endereço de retirada.", nameof(pickupAddress));
        OriginPostalCode = origin;
        PickupEnabled = pickupEnabled;
        PickupAddress = address;
    }

    /// <summary>Empresa de desmontagem (Lei 12.977, RF10 CA2): rastreabilidade obrigatória para pôr peça usada à venda.</summary>
    public bool DismantlerMode { get; private set; }

    /// <summary>Campos extras de rastreabilidade das peças (RF10 CA1).</summary>
    public List<Catalog.TraceabilityField> TraceabilityFields { get; private set; } = [];

    public void ConfigureTraceability(bool dismantlerMode, IReadOnlyList<Catalog.TraceabilityField> fields)
    {
        TraceabilityFields = Catalog.Traceability.NormalizeFields(fields).ToList();
        DismantlerMode = dismantlerMode;
    }

    /// <summary>E-mail de contato da loja: resposta dos e-mails enviados pelo remetente da plataforma (Q38).</summary>
    public string? ContactEmail { get; private set; }

    /// <summary>SMTP da própria loja; nulo = remetente padrão da plataforma. A senha fica no cofre.</summary>
    public Email.SmtpServer? Smtp { get; private set; }

    public void ConfigureEmail(string? contactEmail, Email.SmtpServer? smtp)
    {
        var contact = Email.StoreEmailRules.NormalizeAddress(contactEmail, "E-mail de contato");
        Smtp = smtp is null ? null : Email.StoreEmailRules.Normalize(smtp);
        ContactEmail = contact;
    }

    /// <summary>Configuração fiscal (RF19); nulo = <see cref="Fiscal.FiscalSettings.Default"/> (emissão desligada).</summary>
    public Domain.Fiscal.FiscalSettings? Fiscal { get; private set; }

    public Domain.Fiscal.FiscalSettings FiscalOrDefault => Fiscal ?? Domain.Fiscal.FiscalSettings.Default;

    public void ConfigureFiscal(Domain.Fiscal.FiscalSettings settings) => Fiscal = Domain.Fiscal.FiscalRules.Normalize(settings);

    /// <summary>
    /// RF31 CA2/CA4: perguntas simples de compradores respondidas pela IA sem revisão. HIPÓTESE: desligada por padrão; as
    /// complexas e as de compatibilidade sempre viram rascunho.
    /// </summary>
    public bool AutoAnswerQuestions { get; private set; }

    public void SetAutoAnswerQuestions(bool enabled) => AutoAnswerQuestions = enabled;

    public const int DefaultDailySummaryHour = 7;

    /// <summary>RF34: resumo diário por e-mail aos Donos. HIPÓTESE: ligado por padrão (o requisito dá o horário padrão).</summary>
    public bool DailySummaryEnabled { get; private set; } = true;

    /// <summary>Hora cheia (America/Sao_Paulo) a partir da qual o resumo de ontem sai (RF34 CA1: padrão 07:00).</summary>
    public int DailySummaryHour { get; private set; } = DefaultDailySummaryHour;

    public void ConfigureDailySummary(bool enabled, int hour)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(hour, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(hour, 23);
        DailySummaryEnabled = enabled;
        DailySummaryHour = hour;
    }

    public void SetReservationMinutes(int minutes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, 24 * 60);
        ReservationMinutes = minutes;
    }
}
