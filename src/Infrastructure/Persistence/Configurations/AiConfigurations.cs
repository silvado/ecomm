using Ecommerce.Domain.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

/// <summary>Registro de chamadas de IA (RF35): consultado por loja, período e funcionalidade.</summary>
internal sealed class AiInteractionConfiguration : IEntityTypeConfiguration<AiInteraction>
{
    public void Configure(EntityTypeBuilder<AiInteraction> builder)
    {
        builder.ToTable("ai_interactions");
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Feature).HasMaxLength(AiInteraction.FeatureMaxLength).IsRequired();
        builder.Property(i => i.Model).HasMaxLength(AiInteraction.ModelMaxLength).IsRequired();
        builder.Property(i => i.EstimatedCostUsd).HasPrecision(12, 6);
        builder.Property(i => i.InputSummary).HasMaxLength(AiInteraction.SummaryMaxLength).IsRequired();
        builder.Property(i => i.Output).HasMaxLength(AiInteraction.OutputMaxLength);
        builder.Property(i => i.Error).HasMaxLength(AiInteraction.ErrorMaxLength);
        builder.Property(i => i.CorrelationId).HasMaxLength(64);
        builder.HasIndex(i => new { i.TenantId, i.At });
        builder.HasIndex(i => new { i.TenantId, i.Feature, i.At });
    }
}

/// <summary>Uso mensal por funcionalidade (RF36): uma linha por loja, funcionalidade e mês, atualizada só por SQL atômico.</summary>
internal sealed class AiUsageCounterConfiguration : IEntityTypeConfiguration<AiUsageCounter>
{
    public void Configure(EntityTypeBuilder<AiUsageCounter> builder)
    {
        builder.ToTable("ai_usage_counters", t => t.HasCheckConstraint("ck_ai_usage_counters_used", "used >= 0"));
        builder.HasKey(c => new { c.TenantId, c.Feature, c.Period });
        builder.Property(c => c.Feature).HasMaxLength(AiInteraction.FeatureMaxLength);
    }
}
