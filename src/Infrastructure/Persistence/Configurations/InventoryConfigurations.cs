using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

internal sealed class StockConfiguration : IEntityTypeConfiguration<Stock>
{
    public void Configure(EntityTypeBuilder<Stock> builder)
    {
        builder.HasKey(s => s.PartId);
        builder.HasOne<Part>().WithOne().HasForeignKey<Stock>(s => s.PartId).OnDelete(DeleteBehavior.Cascade);
        builder.Ignore(s => s.Available);
        builder.HasIndex(s => s.TenantId);
        // Última barreira do RNF02: o banco recusa qualquer saldo negativo ou reserva maior que o físico.
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_stocks_on_hand_non_negative", "on_hand >= 0");
            t.HasCheckConstraint("ck_stocks_reserved_non_negative", "reserved >= 0");
            t.HasCheckConstraint("ck_stocks_reserved_within_on_hand", "reserved <= on_hand");
        });
    }
}

internal sealed class StockReservationConfiguration : IEntityTypeConfiguration<StockReservation>
{
    public void Configure(EntityTypeBuilder<StockReservation> builder)
    {
        builder.HasKey(r => r.Id);
        builder.HasOne<Part>().WithMany().HasForeignKey(r => r.PartId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(r => r.Reference).HasMaxLength(100).IsRequired();
        builder.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(r => new { r.TenantId, r.Reference });
        builder.ToTable(t => t.HasCheckConstraint("ck_stock_reservations_quantity_positive", "quantity > 0"));
    }
}

internal sealed class StockMovementConfiguration : IEntityTypeConfiguration<StockMovement>
{
    public void Configure(EntityTypeBuilder<StockMovement> builder)
    {
        builder.HasKey(m => m.Id);
        builder.HasOne<Part>().WithMany().HasForeignKey(m => m.PartId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(m => m.Reason).HasConversion<string>().HasMaxLength(30);
        builder.Property(m => m.Reference).HasMaxLength(100).IsRequired();
        builder.HasIndex(m => new { m.TenantId, m.PartId, m.At });
    }
}

internal sealed class StoreSettingsConfiguration : IEntityTypeConfiguration<StoreSettings>
{
    public void Configure(EntityTypeBuilder<StoreSettings> builder) => builder.HasKey(s => s.TenantId);
}
