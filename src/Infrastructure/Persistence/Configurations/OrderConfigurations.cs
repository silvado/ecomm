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
        builder.Property(o => o.BuyerCnpj).HasMaxLength(14);
        builder.Property(o => o.PaymentMethod).HasConversion<string>().HasMaxLength(20);
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
        builder.Property(o => o.CancelReason).HasMaxLength(CustomerOrder.CancelReasonMaxLength);
        builder.Property(o => o.CanceledBy).HasConversion<string>().HasMaxLength(20);
        builder.Property(o => o.ReviewReason).HasMaxLength(300);
        builder.Property(o => o.TrackingCode).HasMaxLength(CustomerOrder.TrackingCodeMaxLength);
        builder.Property(o => o.ExternalId).HasMaxLength(CustomerOrder.ExternalIdMaxLength);
        builder.Property(o => o.HasReturns).HasDefaultValue(false);
        builder.Ignore(o => o.AcceptsReturn);
        builder.Ignore(o => o.Address);
        builder.HasMany(o => o.Items).WithOne().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(o => o.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

        builder.HasIndex(o => new { o.TenantId, o.Number }).IsUnique();
        // Idempotência do checkout: o mesmo token (envio repetido) não cria outro pedido.
        builder.HasIndex(o => new { o.TenantId, o.AccessTokenHash }).IsUnique();
        builder.HasIndex(o => new { o.TenantId, o.PlacedAt });
        // Área do cliente (RF16): pedidos do e-mail do comprador.
        builder.HasIndex(o => new { o.TenantId, o.BuyerEmail, o.PlacedAt }).HasFilter("buyer_email <> ''");
        // RF17 CA2: pedido de canal externo é único por loja, canal e id no canal.
        builder.HasIndex(o => new { o.TenantId, o.Origin, o.ExternalId }).IsUnique().HasFilter("external_id IS NOT NULL");
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_customer_orders_totals", "items_total > 0 AND shipping_total >= 0 AND total = items_total + shipping_total");
            t.HasCheckConstraint("ck_customer_orders_counter_payment", "(origin = 'Counter') = (payment_method IS NOT NULL)");
            // Venda de canal (Mercado Livre): endereço e frete ficam no canal, que faz o envio.
            t.HasCheckConstraint("ck_customer_orders_delivery",
                "(delivery_method = 'Shipping' AND (delivery_postal_code IS NOT NULL OR anonymized_at IS NOT NULL) AND shipping_service_id IS NOT NULL)"
                + " OR (delivery_method = 'Shipping' AND origin = 'MercadoLivre')"
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

internal sealed class OrderHistoryConfiguration : IEntityTypeConfiguration<OrderHistoryEntry>
{
    public void Configure(EntityTypeBuilder<OrderHistoryEntry> builder)
    {
        builder.ToTable("order_history");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.Id).ValueGeneratedNever();
        builder.HasOne<CustomerOrder>().WithMany().HasForeignKey(h => h.OrderId).OnDelete(DeleteBehavior.Cascade);
        builder.Property(h => h.FromStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(h => h.ToStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(h => h.Actor).HasConversion<string>().HasMaxLength(20);
        builder.Property(h => h.Note).HasMaxLength(OrderHistoryEntry.NoteMaxLength);
        builder.HasIndex(h => new { h.TenantId, h.OrderId, h.At });
    }
}

internal sealed class OrderReturnConfiguration : IEntityTypeConfiguration<OrderReturn>
{
    public void Configure(EntityTypeBuilder<OrderReturn> builder)
    {
        builder.ToTable("order_returns", t => t.HasCheckConstraint("ck_order_returns_refund", "refund_amount IS NULL OR refund_amount >= 0"));
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).ValueGeneratedNever();
        builder.HasOne<CustomerOrder>().WithMany().HasForeignKey(r => r.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(r => r.Lines).AsJson("'[]'::jsonb");
        builder.Property(r => r.Reason).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.RefundMethod).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.RefundAmount).HasPrecision(12, 2);
        builder.Property(r => r.Note).HasMaxLength(OrderReturn.NoteMaxLength);
        builder.HasIndex(r => new { r.TenantId, r.OrderId, r.At });
    }
}
internal sealed class OrderCounterConfiguration : IEntityTypeConfiguration<OrderCounter>
{
    public void Configure(EntityTypeBuilder<OrderCounter> builder) => builder.HasKey(c => c.TenantId);
}
