using Ecommerce.Domain.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

internal sealed class StoreBrandingConfiguration : IEntityTypeConfiguration<StoreBranding>
{
    public void Configure(EntityTypeBuilder<StoreBranding> builder)
    {
        builder.HasKey(b => b.TenantId);
        builder.Property(b => b.PrimaryColor).HasMaxLength(7).IsRequired();
        builder.Property(b => b.BackgroundColor).HasMaxLength(7).IsRequired();
        builder.Property(b => b.TextColor).HasMaxLength(7).IsRequired();
        builder.Property(b => b.About).HasMaxLength(StoreBranding.LongTextMaxLength).IsRequired();
        builder.Property(b => b.ReturnPolicy).HasMaxLength(StoreBranding.LongTextMaxLength).IsRequired();
        builder.Property(b => b.Footer).HasMaxLength(StoreBranding.FooterMaxLength).IsRequired();
        builder.Ignore(b => b.OnPrimaryColor);
    }
}
