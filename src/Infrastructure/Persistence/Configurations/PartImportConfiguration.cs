using System.Text.Json;
using Ecommerce.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

/// <summary>Importação de peças (RF11): linhas válidas, erros e pulados em jsonb — lidos e gravados inteiros com a importação.</summary>
internal sealed class PartImportConfiguration : IEntityTypeConfiguration<PartImport>
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public void Configure(EntityTypeBuilder<PartImport> builder)
    {
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.FileName).HasMaxLength(PartImport.FileNameMaxLength).IsRequired();
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
        JsonList(builder.Property(i => i.Rows));
        JsonList(builder.Property(i => i.Errors));
        JsonList(builder.Property(i => i.Skipped));
        builder.Ignore(i => i.ValidRows);
        builder.HasIndex(i => new { i.TenantId, i.CreatedAt });
        builder.ToTable(t => t.HasCheckConstraint("ck_part_imports_progress", "processed_rows >= 0 AND imported_count >= 0 AND imported_count <= processed_rows"));
    }

    private static void JsonList<T>(PropertyBuilder<List<T>> property) =>
        property.HasColumnType("jsonb").IsRequired()
            .HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<List<T>>(v, Json) ?? new List<T>(),
                new ValueComparer<List<T>>(
                    (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
                    v => JsonSerializer.Serialize(v, Json).GetHashCode(StringComparison.Ordinal),
                    v => JsonSerializer.Deserialize<List<T>>(JsonSerializer.Serialize(v, Json), Json)!));
}
