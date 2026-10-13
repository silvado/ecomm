using Ecommerce.Domain.Fiscal;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

/// <summary>NF-e dos pedidos (RF19): uma ativa (processando ou autorizada) por pedido; referência única na loja.</summary>
internal sealed class FiscalDocumentConfiguration : IEntityTypeConfiguration<FiscalDocument>
{
    public void Configure(EntityTypeBuilder<FiscalDocument> builder)
    {
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.Source).HasConversion<string>().HasMaxLength(20);
        builder.Property(d => d.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(d => d.Environment).HasConversion<string>().HasMaxLength(20);
        builder.Property(d => d.Reference).HasMaxLength(FiscalDocument.ReferenceMaxLength);
        builder.Property(d => d.Provider).HasMaxLength(40);
        builder.Property(d => d.Recipient).AsOptionalJson();
        builder.Property(d => d.AccessKey).HasMaxLength(AccessKey.Length);
        builder.Property(d => d.Message).HasMaxLength(FiscalDocument.MessageMaxLength);
        builder.Property(d => d.XmlStorageKey).HasMaxLength(200);
        builder.Property(d => d.DanfeStorageKey).HasMaxLength(200);
        builder.Property(d => d.CancelJustification).HasMaxLength(FiscalDocument.JustificationMaxLength);
        builder.HasIndex(d => new { d.TenantId, d.OrderId, d.RequestedAt });
        builder.HasIndex(d => new { d.TenantId, d.Reference }).IsUnique().HasFilter("reference IS NOT NULL");
        // Duas emissões ao mesmo tempo para o mesmo pedido: o índice recusa a segunda.
        builder.HasIndex(d => new { d.TenantId, d.OrderId }).IsUnique().HasDatabaseName("ix_fiscal_documents_tenant_id_order_id_active")
            .HasFilter("status IN ('Processing', 'Authorized')");
        builder.HasOne<Domain.Orders.CustomerOrder>().WithMany().HasForeignKey(d => d.OrderId).OnDelete(DeleteBehavior.Restrict);
    }
}
