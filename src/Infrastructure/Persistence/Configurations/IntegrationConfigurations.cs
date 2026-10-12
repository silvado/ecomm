using Ecommerce.Domain.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

/// <summary>Log de integração (RF29): consultado por pedido, por peça e por período; apagado depois de 90 dias.</summary>
internal sealed class IntegrationLogConfiguration : IEntityTypeConfiguration<IntegrationLogEntry>
{
    public void Configure(EntityTypeBuilder<IntegrationLogEntry> builder)
    {
        builder.ToTable("integration_logs");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.Channel).HasMaxLength(IntegrationLogEntry.ChannelMaxLength).IsRequired();
        builder.Property(l => l.Operation).HasMaxLength(IntegrationLogEntry.OperationMaxLength).IsRequired();
        builder.Property(l => l.ErrorSummary).HasMaxLength(IntegrationLogEntry.ErrorMaxLength);
        builder.Property(l => l.Reference).HasMaxLength(IntegrationLogEntry.ReferenceMaxLength);
        builder.Property(l => l.CorrelationId).HasMaxLength(64);
        builder.HasIndex(l => new { l.TenantId, l.At });
        builder.HasIndex(l => new { l.TenantId, l.OrderId }).HasFilter("order_id IS NOT NULL");
        builder.HasIndex(l => new { l.TenantId, l.PartId }).HasFilter("part_id IS NOT NULL");
        builder.HasIndex(l => new { l.TenantId, l.CorrelationId }).HasFilter("correlation_id IS NOT NULL");
    }
}

/// <summary>Falhas de processamento em segundo plano (RF28): a loja vê e reprocessa.</summary>
internal sealed class MessageFailureConfiguration : IEntityTypeConfiguration<MessageFailure>
{
    public void Configure(EntityTypeBuilder<MessageFailure> builder)
    {
        builder.ToTable("message_failures");
        builder.HasKey(f => f.Id);
        builder.Property(f => f.Id).ValueGeneratedNever();
        builder.Property(f => f.MessageType).HasMaxLength(200).IsRequired();
        builder.Property(f => f.Description).HasMaxLength(MessageFailure.DescriptionMaxLength).IsRequired();
        builder.Property(f => f.ErrorType).HasMaxLength(200).IsRequired();
        builder.Property(f => f.ErrorSummary).HasMaxLength(MessageFailure.ErrorMaxLength);
        builder.Property(f => f.CorrelationId).HasMaxLength(64);
        builder.Property(f => f.Status).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(f => new { f.TenantId, f.Status, f.FailedAt });
    }
}
