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

    public void SetReservationMinutes(int minutes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, 24 * 60);
        ReservationMinutes = minutes;
    }
}
