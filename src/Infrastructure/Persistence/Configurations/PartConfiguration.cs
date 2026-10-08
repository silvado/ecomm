using Ecommerce.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

internal sealed class PartConfiguration : IEntityTypeConfiguration<Part>
{
    public void Configure(EntityTypeBuilder<Part> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.InternalCode).HasMaxLength(60).IsRequired();
        builder.Property(p => p.Title).HasMaxLength(200).IsRequired();
        builder.Property(p => p.Price).HasPrecision(12, 2);
        builder.HasIndex(p => new { p.TenantId, p.InternalCode }).IsUnique();
    }
}
