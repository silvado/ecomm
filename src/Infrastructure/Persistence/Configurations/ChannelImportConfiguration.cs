using Ecommerce.Domain.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

/// <summary>Importações de anúncios do canal (RF11 CA2/CA3): ids e relatório em JSON, progresso nas colunas.</summary>
internal sealed class ChannelImportConfiguration : IEntityTypeConfiguration<ChannelImport>
{
    public void Configure(EntityTypeBuilder<ChannelImport> builder)
    {
        builder.HasKey(i => i.Id);
        builder.Property(i => i.Id).ValueGeneratedNever();
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(i => i.ExternalIds).AsJson("'[]'::jsonb");
        builder.Property(i => i.Issues).AsJson("'[]'::jsonb");
        builder.Property(i => i.Cursor).HasMaxLength(500);
        builder.Property(i => i.FailureReason).HasMaxLength(300);
        builder.Ignore(i => i.TotalItems);
        builder.Ignore(i => i.IsRunning);
        builder.HasIndex(i => new { i.TenantId, i.CreatedAt });
        builder.ToTable(t => t.HasCheckConstraint("ck_channel_imports_progress", "processed_items >= 0"));
    }
}
