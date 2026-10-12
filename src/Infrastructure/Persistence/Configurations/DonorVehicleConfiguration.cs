using Ecommerce.Domain.Catalog;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Ecommerce.Infrastructure.Persistence.Configurations;

/// <summary>Veículo doador (RF10): placa, chassi e Renavam únicos na loja quando informados.</summary>
internal sealed class DonorVehicleConfiguration : IEntityTypeConfiguration<DonorVehicle>
{
    public void Configure(EntityTypeBuilder<DonorVehicle> builder)
    {
        builder.HasKey(v => v.Id);
        builder.Property(v => v.Id).ValueGeneratedNever();
        builder.Property(v => v.Plate).HasMaxLength(7);
        builder.Property(v => v.Chassis).HasMaxLength(17);
        builder.Property(v => v.Renavam).HasMaxLength(11);
        builder.Property(v => v.Brand).HasMaxLength(DonorVehicle.TextMaxLength).IsRequired();
        builder.Property(v => v.Model).HasMaxLength(DonorVehicle.TextMaxLength).IsRequired();
        builder.Property(v => v.Color).HasMaxLength(DonorVehicle.ColorMaxLength);
        builder.Property(v => v.DeregistrationCertificate).HasMaxLength(DonorVehicle.CertificateMaxLength);
        builder.Property(v => v.EntryInvoiceKey).HasMaxLength(44);
        builder.Property(v => v.Notes).HasMaxLength(DonorVehicle.NotesMaxLength);
        builder.Ignore(v => v.Label);
        builder.Ignore(v => v.IsCompleteForDismantler);
        builder.HasIndex(v => new { v.TenantId, v.Plate }).IsUnique().HasFilter("plate IS NOT NULL");
        builder.HasIndex(v => new { v.TenantId, v.Chassis }).IsUnique().HasFilter("chassis IS NOT NULL");
        builder.HasIndex(v => new { v.TenantId, v.Renavam }).IsUnique().HasFilter("renavam IS NOT NULL");
        builder.HasIndex(v => new { v.TenantId, v.CreatedAt });
        builder.ToTable(t => t.HasCheckConstraint("ck_donor_vehicles_identified", "plate IS NOT NULL OR chassis IS NOT NULL"));
    }
}
