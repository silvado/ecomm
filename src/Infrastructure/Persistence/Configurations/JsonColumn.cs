using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

/// <summary>Coluna jsonb com um valor .NET lido e gravado inteiro (listas e dicionários pequenos da própria entidade).</summary>
internal static class JsonColumn
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static PropertyBuilder<T> AsJson<T>(this PropertyBuilder<T> property, string emptySql) where T : class, new() =>
        property.HasColumnType("jsonb").IsRequired().HasDefaultValueSql(emptySql)
            .HasConversion(
                v => JsonSerializer.Serialize(v, Json),
                v => JsonSerializer.Deserialize<T>(v, Json) ?? new T(),
                new ValueComparer<T>(
                    (a, b) => JsonSerializer.Serialize(a, Json) == JsonSerializer.Serialize(b, Json),
                    v => JsonSerializer.Serialize(v, Json).GetHashCode(StringComparison.Ordinal),
                    v => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(v, Json), Json)!));
}
