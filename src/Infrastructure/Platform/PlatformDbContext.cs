using Ecommerce.Domain.Platform;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Platform;

/// <summary>
/// Banco <c>plataforma</c>: catálogo de tenants, domínios e (futuramente) identidade, planos e assinaturas.
/// Sem RLS — acessado apenas pela resolução de tenant e por casos de uso de superadmin (ADR-0001).
/// </summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantDomain> TenantDomains => Set<TenantDomain>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Tenant>(b =>
        {
            b.HasKey(t => t.Id);
            b.Property(t => t.Slug).HasMaxLength(40).IsRequired();
            b.HasIndex(t => t.Slug).IsUnique();
            b.Property(t => t.Cnpj).HasConversion(c => c.Value, v => Cnpj.Parse(v)).HasMaxLength(14).IsRequired();
            b.HasIndex(t => t.Cnpj).IsUnique();
            b.Property(t => t.LegalName).HasMaxLength(200).IsRequired();
            b.Property(t => t.TradeName).HasMaxLength(120).IsRequired();
            b.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
            b.Property(t => t.DatabaseKey).HasMaxLength(60).IsRequired();
            b.HasMany(t => t.Domains).WithOne().HasForeignKey(d => d.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(t => t.Domains).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<TenantDomain>(b =>
        {
            b.HasKey(d => d.Id);
            b.Property(d => d.Host).HasMaxLength(253).IsRequired();
            b.HasIndex(d => d.Host).IsUnique();
            b.Property(d => d.Kind).HasConversion<string>().HasMaxLength(30);
            b.Property(d => d.VerificationStatus).HasConversion<string>().HasMaxLength(20);
        });
    }
}
