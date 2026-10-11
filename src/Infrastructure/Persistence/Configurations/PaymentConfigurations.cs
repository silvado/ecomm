using Ecommerce.Domain.Orders;
using Ecommerce.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

internal sealed class PaymentConfiguration : IEntityTypeConfiguration<Payment>
{
    public void Configure(EntityTypeBuilder<Payment> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.HasOne<CustomerOrder>().WithMany().HasForeignKey(p => p.OrderId).OnDelete(DeleteBehavior.Restrict);
        builder.Property(p => p.Gateway).HasMaxLength(Payment.GatewayMaxLength).IsRequired();
        builder.Property(p => p.ExternalId).HasMaxLength(Payment.ExternalIdMaxLength).IsRequired();
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.Amount).HasPrecision(12, 2);
        builder.Property(p => p.Method).HasMaxLength(30);
        builder.HasIndex(p => new { p.TenantId, p.Gateway, p.ExternalId }).IsUnique();
        builder.HasIndex(p => new { p.TenantId, p.OrderId });
    }
}

internal sealed class ExternalEventConfiguration : IEntityTypeConfiguration<ExternalEvent>
{
    public void Configure(EntityTypeBuilder<ExternalEvent> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Source).HasMaxLength(ExternalEvent.SourceMaxLength).IsRequired();
        builder.Property(e => e.IdempotencyKey).HasMaxLength(ExternalEvent.KeyMaxLength).IsRequired();
        builder.Property(e => e.ResourceId).HasMaxLength(ExternalEvent.ResourceIdMaxLength).IsRequired();
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
        // RF26: a mesma notificação entregue de novo esbarra aqui e não tem efeito.
        builder.HasIndex(e => new { e.TenantId, e.Source, e.IdempotencyKey }).IsUnique();
    }
}
