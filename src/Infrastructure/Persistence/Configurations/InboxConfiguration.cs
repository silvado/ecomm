using Ecommerce.Domain.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

/// <summary>
/// Inbox de avisos de canais (RF26), no schema <c>inbox</c>, sem RLS (exceção do ADR-0001: ainda não há loja). A chave
/// única (origem, chave) descarta reentregas, inclusive simultâneas.
/// </summary>
internal sealed class InboxEventConfiguration : IEntityTypeConfiguration<InboxEvent>
{
    public const string Schema = "inbox";
    public const string Table = "external_events";

    public void Configure(EntityTypeBuilder<InboxEvent> builder)
    {
        builder.ToTable(Table, Schema);
        // Nomes próprios: o EF trata nomes de restrição como globais e confundiria com public.external_events.
        builder.HasKey(e => e.Id).HasName("pk_inbox_external_events");
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.Source).HasMaxLength(InboxEvent.SourceMaxLength).IsRequired();
        builder.Property(e => e.IdempotencyKey).HasMaxLength(InboxEvent.KeyMaxLength).IsRequired();
        builder.Property(e => e.ExternalAccount).HasMaxLength(InboxEvent.AccountMaxLength).IsRequired();
        builder.Property(e => e.Topic).HasMaxLength(InboxEvent.TopicMaxLength).IsRequired();
        builder.Property(e => e.Resource).HasMaxLength(InboxEvent.ResourceMaxLength).IsRequired();
        builder.Property(e => e.Payload).HasMaxLength(InboxEvent.PayloadMaxLength);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
        builder.HasIndex(e => new { e.Source, e.IdempotencyKey }).IsUnique().HasDatabaseName("ix_inbox_external_events_source_idempotency_key");
        builder.HasIndex(e => new { e.Status, e.ReceivedAt }).HasDatabaseName("ix_inbox_external_events_status_received_at");
    }
}
