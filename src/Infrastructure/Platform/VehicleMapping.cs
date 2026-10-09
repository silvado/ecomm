using Ecommerce.Domain.Vehicles;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Platform;

/// <summary>
/// Mesmo mapeamento dos veículos nos dois bancos: fonte no <c>plataforma</c> (schema padrão) e réplica no schema
/// <c>ref</c> do banco de lojas — com os mesmos ids, para <c>part_compatibilities</c> ter chave estrangeira local.
/// </summary>
internal static class VehicleMapping
{
    public const string RefSchema = "ref";

    public static void Configure(ModelBuilder modelBuilder, string? schema)
    {
        modelBuilder.Entity<VehicleBrand>(b =>
        {
            b.ToTable("vehicle_brands", schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(VehicleBrand.NameMaxLength).IsRequired();
            b.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<VehicleModel>(b =>
        {
            b.ToTable("vehicle_models", schema);
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Name).HasMaxLength(VehicleModel.NameMaxLength).IsRequired();
            b.HasIndex(x => new { x.BrandId, x.Name }).IsUnique();
            b.HasOne<VehicleBrand>().WithMany().HasForeignKey(x => x.BrandId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<VehicleVersion>(b =>
        {
            b.ToTable("vehicle_versions", schema, t => t.HasCheckConstraint("ck_vehicle_versions_years", "year_to IS NULL OR year_to >= year_from"));
            b.HasKey(x => x.Id);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Engine).HasMaxLength(VehicleVersion.EngineMaxLength).IsRequired();
            b.Property(x => x.MlReference).HasMaxLength(40);
            b.HasIndex(x => new { x.ModelId, x.Engine, x.YearFrom }).IsUnique();
            b.HasOne<VehicleModel>().WithMany().HasForeignKey(x => x.ModelId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
