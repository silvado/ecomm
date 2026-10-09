using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Domain.Vehicles;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Platform;

/// <summary>
/// Banco <c>plataforma</c>: catálogo de tenants, domínios, planos e identidade dos usuários do painel.
/// Sem RLS — acessado apenas pela resolução de tenant, login e casos de uso de superadmin (ADR-0001).
/// Tabelas com tenant_id aqui (vínculos, sessões) são sempre filtradas pelo tenant explicitamente.
/// </summary>
public sealed class PlatformDbContext(DbContextOptions<PlatformDbContext> options) : DbContext(options)
{
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantDomain> TenantDomains => Set<TenantDomain>();
    public DbSet<Plan> Plans => Set<Plan>();
    public DbSet<PlanLimit> PlanLimits => Set<PlanLimit>();
    public DbSet<UserAccount> UserAccounts => Set<UserAccount>();
    public DbSet<TenantMembership> TenantMemberships => Set<TenantMembership>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<VehicleBrand> VehicleBrands => Set<VehicleBrand>();
    public DbSet<VehicleModel> VehicleModels => Set<VehicleModel>();
    public DbSet<VehicleVersion> VehicleVersions => Set<VehicleVersion>();

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
            b.Property(t => t.PlanCode).HasMaxLength(30).IsRequired().HasDefaultValue(Plan.Essential);
            b.HasOne<Plan>().WithMany().HasForeignKey(t => t.PlanCode).HasPrincipalKey(p => p.Code).OnDelete(DeleteBehavior.Restrict);
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

        modelBuilder.Entity<Plan>(b =>
        {
            b.HasKey(p => p.Id);
            b.Property(p => p.Code).HasMaxLength(30).IsRequired();
            b.Property(p => p.Name).HasMaxLength(60).IsRequired();
            b.HasMany(p => p.Limits).WithOne().HasForeignKey(l => l.PlanCode).HasPrincipalKey(p => p.Code).OnDelete(DeleteBehavior.Cascade);
            b.Navigation(p => p.Limits).UsePropertyAccessMode(PropertyAccessMode.Field);
            b.HasData(
                new Plan(new Guid("0199c4a0-0000-7000-8000-000000000001"), Plan.Essential, "Essencial"),
                new Plan(new Guid("0199c4a0-0000-7000-8000-000000000002"), Plan.Professional, "Profissional"),
                new Plan(new Guid("0199c4a0-0000-7000-8000-000000000003"), Plan.Complete, "Completo"));
        });

        modelBuilder.Entity<PlanLimit>(b =>
        {
            b.HasKey(l => new { l.PlanCode, l.Key });
            b.Property(l => l.Key).HasMaxLength(40);
            // HIPÓTESE (requisitos → Em aberto): 2 / 5 / 10 usuários.
            b.HasData(
                new PlanLimit(Plan.Essential, PlanLimit.Users, 2),
                new PlanLimit(Plan.Professional, PlanLimit.Users, 5),
                new PlanLimit(Plan.Complete, PlanLimit.Users, 10));
        });

        modelBuilder.Entity<UserAccount>(b =>
        {
            b.HasKey(u => u.Id);
            b.Property(u => u.Email).HasMaxLength(254).IsRequired();
            b.HasIndex(u => u.Email).IsUnique();
            b.Property(u => u.PasswordHash).HasMaxLength(200).IsRequired();
        });

        modelBuilder.Entity<TenantMembership>(b =>
        {
            b.HasKey(m => new { m.TenantId, m.UserId });
            b.HasIndex(m => m.UserId);
            b.Property(m => m.Role).HasConversion<string>().HasMaxLength(20);
            b.HasOne<Tenant>().WithMany().HasForeignKey(m => m.TenantId).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<UserAccount>().WithMany().HasForeignKey(m => m.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // Fonte da tabela de veículos (RF09 CA2); replicada para ref.* em cada banco de lojas.
        VehicleMapping.Configure(modelBuilder, schema: null);

        modelBuilder.Entity<RefreshToken>(b =>
        {
            b.HasKey(t => t.Id);
            b.HasIndex(t => t.TokenHash).IsUnique();
            b.HasIndex(t => t.FamilyId);
            b.HasIndex(t => new { t.UserId, t.TenantId });
            b.HasOne<UserAccount>().WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });
    }
}
