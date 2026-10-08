using Ecommerce.Domain.Vault;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

internal sealed class TenantSecretConfiguration : IEntityTypeConfiguration<TenantSecret>
{
    public void Configure(EntityTypeBuilder<TenantSecret> builder)
    {
        builder.HasKey(s => s.Id);
        builder.Property(s => s.Kind).HasConversion<string>().HasMaxLength(40);
        builder.Property(s => s.Name).HasMaxLength(100).IsRequired();
        builder.Property(s => s.KeyVersion).HasMaxLength(20).IsRequired();
        builder.Property(s => s.Hint).HasMaxLength(4);
        builder.HasIndex(s => new { s.TenantId, s.Kind, s.Name }).IsUnique();
    }
}

internal sealed class TenantDataKeyConfiguration : IEntityTypeConfiguration<TenantDataKey>
{
    public void Configure(EntityTypeBuilder<TenantDataKey> builder)
    {
        builder.HasKey(k => k.TenantId);
        builder.Property(k => k.Version).HasMaxLength(20).IsRequired();
        builder.Property(k => k.MasterKeyVersion).HasMaxLength(20).IsRequired();
    }
}
