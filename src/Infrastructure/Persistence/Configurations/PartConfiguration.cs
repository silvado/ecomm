using Ecommerce.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

internal sealed class PartConfiguration : IEntityTypeConfiguration<Part>
{
    public void Configure(EntityTypeBuilder<Part> builder)
    {
        builder.HasKey(p => p.Id);
        builder.Property(p => p.InternalCode).HasMaxLength(Part.InternalCodeMaxLength).IsRequired();
        builder.Property(p => p.Title).HasMaxLength(Part.TitleMaxLength).IsRequired();
        builder.Property(p => p.Description).HasMaxLength(Part.DescriptionMaxLength).IsRequired();
        builder.Property(p => p.Condition).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(p => p.Price).HasPrecision(12, 2);
        builder.Ignore(p => p.HasShippingDimensions);
        // RF08 CA2: código interno único por tenant, decidido pelo banco (cadastros simultâneos não passam).
        builder.HasIndex(p => new { p.TenantId, p.InternalCode }).IsUnique();
        builder.HasIndex(p => new { p.TenantId, p.Status, p.UpdatedAt });
        builder.HasMany(p => p.OemCodes).WithOne().HasForeignKey(c => c.PartId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.OemCodes).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_parts_price_positive", "price > 0");
            t.HasCheckConstraint("ck_parts_dimensions_positive",
                "coalesce(length_cm, 1) > 0 AND coalesce(width_cm, 1) > 0 AND coalesce(height_cm, 1) > 0 AND coalesce(weight_g, 1) > 0");
        });
    }
}

internal sealed class PartOemCodeConfiguration : IEntityTypeConfiguration<PartOemCode>
{
    public void Configure(EntityTypeBuilder<PartOemCode> builder)
    {
        builder.HasKey(c => new { c.PartId, c.Code });
        builder.Property(c => c.Code).HasMaxLength(PartOemCode.MaxLength);
        // Busca por OEM em toda a loja (atendimento: "tem a peça 1J0615301M?").
        builder.HasIndex(c => new { c.TenantId, c.Code });
    }
}
