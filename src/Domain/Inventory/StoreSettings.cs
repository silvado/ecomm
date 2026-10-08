using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Inventory;

/// <summary>Configurações operacionais da loja. Por enquanto só o prazo de reserva (RF12 CA2).</summary>
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

    public void SetReservationMinutes(int minutes)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(minutes, 5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(minutes, 24 * 60);
        ReservationMinutes = minutes;
    }
}
