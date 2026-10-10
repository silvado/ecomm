using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Inventory;

/// <summary>Configurações operacionais da loja: prazo de reserva (RF12 CA2) e peças sem estoque na vitrine (RF13 CA4).</summary>
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

    public void SetReservationMinutes(int minutes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, 24 * 60);
        ReservationMinutes = minutes;
    }
}
