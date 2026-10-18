using Ecommerce.Domain.Reports;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

/// <summary>Resumos diários enviados (RF34): um por loja e dia — o índice único impede o envio em dobro.</summary>
internal sealed class DailySummaryConfiguration : IEntityTypeConfiguration<DailySummary>
{
    public void Configure(EntityTypeBuilder<DailySummary> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Id).ValueGeneratedNever();
        builder.HasIndex(s => new { s.TenantId, s.Day }).IsUnique();
    }
}

/// <summary>Alertas do guardião enviados (RF33): consultados pelos das últimas 24 horas, para não avisar o mesmo problema em dobro.</summary>
internal sealed class SyncAlertConfiguration : IEntityTypeConfiguration<SyncAlert>
{
    public void Configure(EntityTypeBuilder<SyncAlert> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.HasIndex(a => new { a.TenantId, a.SentAt });
    }
}
