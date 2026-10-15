using Ecommerce.Domain.Privacy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

/// <summary>RNF04: somente inclusão (a migração tira UPDATE/DELETE/TRUNCATE do papel da aplicação); sem FK para o pedido.</summary>
internal sealed class PersonalDataAccessConfiguration : IEntityTypeConfiguration<PersonalDataAccess>
{
    public void Configure(EntityTypeBuilder<PersonalDataAccess> builder)
    {
        builder.ToTable("personal_data_access_log");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.UserEmail).HasMaxLength(254).IsRequired();
        builder.Property(a => a.Action).HasMaxLength(PersonalDataAccess.ActionMaxLength).IsRequired();
        builder.Property(a => a.Details).HasMaxLength(PersonalDataAccess.DetailsMaxLength);
        builder.Property(a => a.Ip).HasMaxLength(45);
        builder.HasIndex(a => new { a.TenantId, a.At });
        builder.HasIndex(a => new { a.TenantId, a.OrderId });
    }
}

/// <summary>RF39: uma exportação pendente por loja (índice único parcial decide a corrida entre dois pedidos).</summary>
internal sealed class DataExportConfiguration : IEntityTypeConfiguration<DataExport>
{
    public void Configure(EntityTypeBuilder<DataExport> builder)
    {
        builder.HasKey(e => e.Id);
        builder.Property(e => e.RequestedByEmail).HasMaxLength(254).IsRequired();
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(e => e.StorageKey).HasMaxLength(200);
        builder.Property(e => e.Error).HasMaxLength(DataExport.ErrorMaxLength);
        builder.HasIndex(e => e.TenantId).IsUnique().HasFilter("status = 'Pending'").HasDatabaseName("ux_data_exports_one_pending");
        builder.HasIndex(e => new { e.TenantId, e.RequestedAt });
    }
}
