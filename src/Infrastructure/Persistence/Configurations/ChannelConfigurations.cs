using Ecommerce.Domain.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

/// <summary>Conexões da loja com canais de venda (E2): uma por canal e conta externa.</summary>
internal sealed class ChannelConnectionConfiguration : IEntityTypeConfiguration<ChannelConnection>
{
    public void Configure(EntityTypeBuilder<ChannelConnection> builder)
    {
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();
        builder.Property(c => c.Channel).HasMaxLength(ChannelConnection.ChannelMaxLength).IsRequired();
        builder.Property(c => c.ExternalAccountId).HasMaxLength(ChannelConnection.ExternalAccountMaxLength).IsRequired();
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(c => new { c.TenantId, c.Channel, c.ExternalAccountId }).IsUnique();
    }
}

/// <summary>Anúncios das peças nos canais (E2): um por peça em cada conexão.</summary>
internal sealed class ChannelListingConfiguration : IEntityTypeConfiguration<ChannelListing>
{
    public void Configure(EntityTypeBuilder<ChannelListing> builder)
    {
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).ValueGeneratedNever();
        builder.Property(l => l.ExternalId).HasMaxLength(ChannelListing.ExternalIdMaxLength);
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(l => l.LastPushedPrice).HasPrecision(12, 2);
        builder.Property(l => l.LastError).HasMaxLength(ChannelListing.ErrorMaxLength);
        builder.HasIndex(l => new { l.TenantId, l.PartId, l.ConnectionId }).IsUnique();
        builder.HasIndex(l => new { l.TenantId, l.ConnectionId, l.ExternalId }).IsUnique().HasFilter("external_id IS NOT NULL");
        builder.HasOne<Domain.Catalog.Part>().WithMany().HasForeignKey(l => l.PartId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ChannelConnection>().WithMany().HasForeignKey(l => l.ConnectionId).OnDelete(DeleteBehavior.Restrict);
    }
}
