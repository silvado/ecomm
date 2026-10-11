using Ecommerce.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

/// <summary>Importação de peças (RF11): linhas válidas, erros e pulados em jsonb — lidos e gravados inteiros com a importação.</summary>
internal sealed class PartImportConfiguration : IEntityTypeConfiguration<PartImport>
{
    public void Configure(EntityTypeBuilder<PartImport> builder)
    {
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.FileName).HasMaxLength(PartImport.FileNameMaxLength).IsRequired();
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(i => i.Rows).AsJson("'[]'::jsonb");
        builder.Property(i => i.Errors).AsJson("'[]'::jsonb");
        builder.Property(i => i.Skipped).AsJson("'[]'::jsonb");
        builder.Ignore(i => i.ValidRows);
        builder.HasIndex(i => new { i.TenantId, i.CreatedAt });
        builder.ToTable(t => t.HasCheckConstraint("ck_part_imports_progress", "processed_rows >= 0 AND imported_count >= 0 AND imported_count <= processed_rows"));
    }
}
