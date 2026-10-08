using System.Linq.Expressions;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Common;
using Ecommerce.Domain.Inventory;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Persistence;

/// <summary>
/// Contexto do banco de dados de tenant (<c>lojas</c>). Defesa em profundidade: filtro global por tenant aqui + RLS no PostgreSQL.
/// </summary>
public sealed class TenantDbContext(DbContextOptions<TenantDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public DbSet<Part> Parts => Set<Part>();
    public DbSet<Stock> Stocks => Set<Stock>();
    public DbSet<StockReservation> StockReservations => Set<StockReservation>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<StoreSettings> StoreSettings => Set<StoreSettings>();

    /// <summary>Tenant visto por este contexto (o mesmo usado pelo filtro global e pelo interceptor de RLS).</summary>
    public Guid? TenantId => tenant.TenantId;

    /// <summary>Tenant do escopo para novas entidades; falha se não houver (nunca gravar dados sem tenant).</summary>
    public Guid RequireTenantId() => tenant.TenantId ?? throw new InvalidOperationException("Operação de tenant sem tenant no escopo.");

    // Lido pelo filtro global a cada consulta.
    private Guid? CurrentTenantId => tenant.TenantId;

    // O interceptor nasce com o mesmo ITenantContext do contexto: não depende de qual escopo de DI montou as opções
    // (o Wolverine pode montar as opções fora do escopo da mensagem — ADR-0002).
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder) =>
        optionsBuilder.AddInterceptors(new TenantConnectionInterceptor(tenant));

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(TenantDbContext).Assembly);

        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantOwned).IsAssignableFrom(entityType.ClrType)) continue;

            var parameter = Expression.Parameter(entityType.ClrType, "e");
            var tenantProperty = Expression.Property(parameter, nameof(ITenantOwned.TenantId));
            var current = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));
            var body = Expression.Equal(Expression.Convert(tenantProperty, typeof(Guid?)), current);
            modelBuilder.Entity(entityType.ClrType).HasQueryFilter(Expression.Lambda(body, parameter));
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        EnsureTenantConsistency();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        EnsureTenantConsistency();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void EnsureTenantConsistency()
    {
        foreach (var entry in ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached) continue;
            if (tenant.TenantId is null || entry.Entity.TenantId != tenant.TenantId)
                throw new InvalidOperationException(
                    $"Gravação de {entry.Metadata.ClrType.Name} fora do tenant do escopo.");
        }
    }
}
