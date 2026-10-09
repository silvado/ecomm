using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Vehicles;
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
        builder.HasMany(p => p.Photos).WithOne().HasForeignKey(f => f.PartId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Photos).HasField("_photos").UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.HasMany(p => p.Compatibilities).WithOne().HasForeignKey(c => c.PartId).OnDelete(DeleteBehavior.Cascade);
        builder.Navigation(p => p.Compatibilities).UsePropertyAccessMode(PropertyAccessMode.Field);
        builder.ToTable(t =>
        {
            t.HasCheckConstraint("ck_parts_price_positive", "price > 0");
            t.HasCheckConstraint("ck_parts_dimensions_positive",
                "coalesce(length_cm, 1) > 0 AND coalesce(width_cm, 1) > 0 AND coalesce(height_cm, 1) > 0 AND coalesce(weight_g, 1) > 0");
        });
    }
}

internal sealed class PartPhotoConfiguration : IEntityTypeConfiguration<PartPhoto>
{
    public void Configure(EntityTypeBuilder<PartPhoto> builder)
    {
        builder.HasKey(p => p.Id);
        // O id vem da aplicação (é a chave dos arquivos no storage): foto nova na coleção da peça é INSERT, não UPDATE.
        builder.Property(p => p.Id).ValueGeneratedNever();
        builder.Property(p => p.OriginalContentType).HasMaxLength(20).IsRequired();
        builder.HasIndex(p => new { p.TenantId, p.PartId, p.Position });
        builder.ToTable(t => t.HasCheckConstraint("ck_part_photos_position", $"position >= 0 AND position < {Part.MaxPhotos}"));
    }
}

internal sealed class PartCompatibilityConfiguration : IEntityTypeConfiguration<PartCompatibility>
{
    public void Configure(EntityTypeBuilder<PartCompatibility> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Source).HasConversion<string>().HasMaxLength(20);
        // Chave estrangeira local para a réplica ref.vehicle_versions; versão em uso não pode sumir.
        builder.HasOne<VehicleVersion>().WithMany().HasForeignKey(c => c.VehicleVersionId).OnDelete(DeleteBehavior.Restrict);
        // A mesma versão com a mesma faixa não se repete na peça (anos nulos = faixa inteira contam como iguais).
        builder.HasIndex(c => new { c.PartId, c.VehicleVersionId, c.YearFrom, c.YearTo }).IsUnique().AreNullsDistinct(false);
        // Busca por veículo na loja: "peças desta loja para esta versão".
        builder.HasIndex(c => new { c.TenantId, c.VehicleVersionId });
        builder.ToTable(t => t.HasCheckConstraint("ck_part_compatibilities_years",
            "(year_from IS NULL AND year_to IS NULL) OR (year_from IS NOT NULL AND year_to IS NOT NULL AND year_from <= year_to)"));
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
