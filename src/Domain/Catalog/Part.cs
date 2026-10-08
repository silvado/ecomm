using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Catalog;

/// <summary>
/// Peça do catálogo. Esqueleto mínimo para a fundação multi-tenant; campos completos no PBI "Cadastro de peça com fotos" (RF08).
/// </summary>
public sealed class Part : ITenantOwned
{
    private Part() { }

    public Part(Guid tenantId, string internalCode, string title, decimal price)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant obrigatório.", nameof(tenantId));
        ArgumentException.ThrowIfNullOrWhiteSpace(internalCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentOutOfRangeException.ThrowIfNegative(price);

        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        InternalCode = internalCode;
        Title = title;
        Price = price;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public string InternalCode { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public decimal Price { get; private set; }

    public void Rename(string title)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        Title = title;
    }
}
