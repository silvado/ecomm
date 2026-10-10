using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Inventory;
using Ecommerce.Domain.Orders;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

internal sealed class CustomerOrderConfiguration : IEntityTypeConfiguration<CustomerOrder>
{
    public void Configure(EntityTypeBuilder<CustomerOrder> builder)
    {
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();
        builder.Property(o => o.Origin).HasConversion<string>().HasMaxLength(20);
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(o => o.AccessTokenHash).HasMaxLength(32).IsRequired();
        builder.Property(o => o.BuyerName).HasMaxLength(CustomerOrder.NameMaxLength).IsRequired();
        builder.Property(o => o.BuyerEmail).HasMaxLength(CustomerOrder.EmailMaxLength).IsRequired();
        builder.Property(o => o.BuyerPhone).HasMaxLength(11).IsRequired();
        builder.Property(o => o.BuyerCpf).HasMaxLength(11).IsRequired();
        builder.Property(o => o.DeliveryMethod).HasConversion<string>().HasMaxLength(20);
        builder.Property(o => o.DeliveryPostalCode).HasMaxLength(8);
        builder.Property(o => o.DeliveryStreet).HasMaxLength(120);
        builder.Property(o => o.DeliveryNumber).HasMaxLength(20);
        builder.Property(o => o.DeliveryComplement).HasMaxLength(60);
        builder.Property(o => o.DeliveryDistrict).HasMaxLength(60);
        builder.Property(o => o.DeliveryCity).HasMaxLength(60);
        builder.Property(o => o.DeliveryState).HasMaxLength(2);
        builder.Property(o => o.ShippingServiceId).HasMaxLength(60);
        builder.Property(o => o.ShippingCarrier).HasMaxLength(100);
        builder.Property(o => o.ShippingService).HasMaxLength(100);
        builder.Property(o => o.PickupAddress).HasMaxLength(StoreSettings.PickupAddressMaxLength);
        builder.Property(o => o.ItemsTotal).HasPrecision(12, 2);
        builder.Property(o => o.ShippingTotal).HasPrecision(12, 2);
        builder.Property(o => o.Total).HasPrecision(12, 2);
        builder.Property(o => o.CancelReason).HasMaxLength(200);
        builder.Ignore(o => o.Address);
        builder.HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(o => o.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(o => new { o.TenantId, o.Number }).IsUnique();
        // Idempotência do checkout: o mesmo token (envio repetido) não cria outro pedido.
        builder.HasIndex(o => new { o.TenantId, o.AccessTokenHash }).IsUnique();
        builder.HasIndex(o => new { o.TenantId, o.PlacedAt });
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_customer_orders_totals", "items_total > 0 AND shipping_total >= 0 AND total = items_total + shipping_total");
            t.HasCheckConstraint("ck_customer_orders_delivery",
                "(delivery_method = 'Shipping' AND delivery_postal_code IS NOT NULL AND shipping_service_id IS NOT NULL)"
                + " OR (delivery_method = 'Pickup' AND pickup_address IS NOT NULL)");
        });
    }
}

internal sealed class OrderItemConfiguration : IEntityTypeConfiguration<OrderItem>
{
    public void Configure(EntityTypeBuilder<OrderItem> builder)
    {
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.HasOne<Part>().WithMany().HasForeignKey(i => i.PartId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<StockReservation>().WithOne().HasForeignKey<OrderItem>(i => i.ReservationId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(i => i.InternalCode).HasMaxLength(Part.InternalCodeMaxLength).IsRequired();
        builder.Property(i => i.Title).HasMaxLength(Part.TitleMaxLength).IsRequired();
        builder.Property(i => i.UnitPrice).HasPrecision(12, 2);
        builder.HasIndex(i => i.TenantId);
        builder.ToTable(t => t.HasCheckConstraint("ck_order_items_positive", "quantity > 0 AND unit_price > 0"));
    }
}

internal sealed class OrderCounterConfiguration : IEntityTypeConfiguration<OrderCounter>
{
    public void Configure(EntityTypeBuilder<OrderCounter> builder) => builder.HasKey(c => c.TenantId);
}
