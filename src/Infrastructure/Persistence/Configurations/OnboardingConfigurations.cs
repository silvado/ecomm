using Ecommerce.Domain.Onboarding;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

/// <summary>Passos opcionais pulados no assistente (RF05); um por passo e loja.</summary>
internal sealed class OnboardingSkipConfiguration : IEntityTypeConfiguration<OnboardingSkip>
{
    public void Configure(EntityTypeBuilder<OnboardingSkip> builder)
    {
        builder.HasKey(s => new { s.TenantId, s.Step });
        builder.Property(s => s.Step).HasConversion<string>().HasMaxLength(30);
    }
}
