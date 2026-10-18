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
        builder.Property(c => c.AccountName).HasMaxLength(ChannelConnection.AccountNameMaxLength);
        builder.Property(c => c.StatusReason).HasMaxLength(ChannelConnection.StatusReasonMaxLength);
        builder.HasIndex(c => new { c.TenantId, c.Channel, c.ExternalAccountId }).IsUnique();
    }
}

/// <summary>Autorizações OAuth em andamento (RF21): procuradas pelo hash do state, na loja do escopo.</summary>
internal sealed class ChannelAuthorizationConfiguration : IEntityTypeConfiguration<ChannelAuthorization>
{
    public void Configure(EntityTypeBuilder<ChannelAuthorization> builder)
    {
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).ValueGeneratedNever();
        builder.Property(a => a.Channel).HasMaxLength(ChannelConnection.ChannelMaxLength).IsRequired();
        builder.Property(a => a.StateHash).HasMaxLength(64).IsRequired();
        builder.Property(a => a.CodeVerifier).HasMaxLength(128).IsRequired();
        builder.HasIndex(a => new { a.TenantId, a.StateHash }).IsUnique();
    }
}

/// <summary>Perguntas de compradores (RF31): uma por pergunta do canal; a lista do painel filtra por situação e data.</summary>
internal sealed class BuyerQuestionConfiguration : IEntityTypeConfiguration<BuyerQuestion>
{
    public void Configure(EntityTypeBuilder<BuyerQuestion> builder)
    {
        builder.HasKey(q => q.Id);
        builder.Property(q => q.Id).ValueGeneratedNever();
        builder.Property(q => q.ExternalId).HasMaxLength(BuyerQuestion.ExternalIdMaxLength).IsRequired();
        builder.Property(q => q.ExternalItemId).HasMaxLength(ChannelListing.ExternalIdMaxLength).IsRequired();
        builder.Property(q => q.Text).HasMaxLength(BuyerQuestion.TextMaxLength).IsRequired();
        builder.Property(q => q.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(q => q.DraftText).HasMaxLength(BuyerQuestion.AnswerMaxLength);
        builder.Property(q => q.Kind).HasConversion<string>().HasMaxLength(20);
        builder.Property(q => q.DraftNote).HasMaxLength(BuyerQuestion.NoteMaxLength);
        builder.Property(q => q.AnswerText).HasMaxLength(BuyerQuestion.AnswerMaxLength);
        builder.Property(q => q.AnsweredVia).HasConversion<string>().HasMaxLength(20);
        builder.Property(q => q.LastError).HasMaxLength(ChannelListing.ErrorMaxLength);
        builder.HasIndex(q => new { q.TenantId, q.ConnectionId, q.ExternalId }).IsUnique();
        builder.HasIndex(q => new { q.TenantId, q.Status, q.AskedAt });
        builder.HasOne<ChannelConnection>().WithMany().HasForeignKey(q => q.ConnectionId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<Domain.Catalog.Part>().WithMany().HasForeignKey(q => q.PartId).OnDelete(DeleteBehavior.Restrict);
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
        builder.Property(l => l.ChannelData).HasColumnType("jsonb");
        builder.HasIndex(l => new { l.TenantId, l.PartId, l.ConnectionId }).IsUnique();
        builder.HasIndex(l => new { l.TenantId, l.ConnectionId, l.ExternalId }).IsUnique().HasFilter("external_id IS NOT NULL");
        builder.HasOne<Domain.Catalog.Part>().WithMany().HasForeignKey(l => l.PartId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ChannelConnection>().WithMany().HasForeignKey(l => l.ConnectionId).OnDelete(DeleteBehavior.Restrict);
    }
}
