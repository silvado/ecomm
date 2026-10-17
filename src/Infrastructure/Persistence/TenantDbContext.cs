using Ecommerce.Domain.Integrations;
using System.Linq.Expressions;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Common;
using Ecommerce.Domain.Customers;
using Ecommerce.Domain.Fiscal;
using Ecommerce.Domain.Inventory;
using Ecommerce.Domain.Orders;
using Ecommerce.Domain.Payments;
using Ecommerce.Domain.Store;
using Ecommerce.Domain.Vehicles;
using Ecommerce.Infrastructure.Platform;
using Ecommerce.Domain.Vault;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Persistence;

/// <summary>
/// Contexto do banco de dados de tenant (<c>lojas</c>). Defesa em profundidade: filtro global por tenant aqui + RLS no PostgreSQL.
/// </summary>
public sealed class TenantDbContext(DbContextOptions<TenantDbContext> options, ITenantContext tenant) : DbContext(options)
{
    public DbSet<Part> Parts => Set<Part>();
    public DbSet<PartOemCode> PartOemCodes => Set<PartOemCode>();
    public DbSet<PartPhoto> PartPhotos => Set<PartPhoto>();
    public DbSet<PartCompatibility> PartCompatibilities => Set<PartCompatibility>();
    public DbSet<PartImport> PartImports => Set<PartImport>();
    public DbSet<DonorVehicle> DonorVehicles => Set<DonorVehicle>();

    /// <summary>Réplica somente leitura (schema ref) da tabela de veículos da plataforma.</summary>
    public DbSet<VehicleBrand> VehicleBrands => Set<VehicleBrand>();
    public DbSet<VehicleModel> VehicleModels => Set<VehicleModel>();
    public DbSet<VehicleVersion> VehicleVersions => Set<VehicleVersion>();
    public DbSet<Stock> Stocks => Set<Stock>();
    public DbSet<StockReservation> StockReservations => Set<StockReservation>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<CustomerOrder> CustomerOrders => Set<CustomerOrder>();
    public DbSet<OrderItem> OrderItems => Set<OrderItem>();
    public DbSet<OrderCounter> OrderCounters => Set<OrderCounter>();
    public DbSet<OrderHistoryEntry> OrderHistory => Set<OrderHistoryEntry>();
    public DbSet<OrderReturn> OrderReturns => Set<OrderReturn>();
    public DbSet<FiscalDocument> FiscalDocuments => Set<FiscalDocument>();
    public DbSet<Domain.Channels.ChannelConnection> ChannelConnections => Set<Domain.Channels.ChannelConnection>();
    public DbSet<Domain.Channels.ChannelListing> ChannelListings => Set<Domain.Channels.ChannelListing>();
    public DbSet<Domain.Channels.ChannelAuthorization> ChannelAuthorizations => Set<Domain.Channels.ChannelAuthorization>();
    public DbSet<CustomerAccessLink> CustomerAccessLinks => Set<CustomerAccessLink>();
    public DbSet<CustomerSession> CustomerSessions => Set<CustomerSession>();
    public DbSet<IntegrationLogEntry> IntegrationLogs => Set<IntegrationLogEntry>();
    public DbSet<Domain.Ai.AiInteraction> AiInteractions => Set<Domain.Ai.AiInteraction>();
    public DbSet<Domain.Ai.AiUsageCounter> AiUsageCounters => Set<Domain.Ai.AiUsageCounter>();

    /// <summary>Avisos de canais antes do roteamento (schema inbox, sem RLS e sem filtro de tenant — RF26, ADR-0001).</summary>
    public DbSet<InboxEvent> InboxEvents => Set<InboxEvent>();
    public DbSet<MessageFailure> MessageFailures => Set<MessageFailure>();
    public DbSet<Payment> Payments => Set<Payment>();
    public DbSet<ExternalEvent> ExternalEvents => Set<ExternalEvent>();
    public DbSet<StoreSettings> StoreSettings => Set<StoreSettings>();
    public DbSet<StoreBranding> StoreBrandings => Set<StoreBranding>();
    public DbSet<TenantSecret> TenantSecrets => Set<TenantSecret>();
    public DbSet<TenantDataKey> TenantDataKeys => Set<TenantDataKey>();
    public DbSet<Domain.Privacy.PersonalDataAccess> PersonalDataAccesses => Set<Domain.Privacy.PersonalDataAccess>();
    public DbSet<Domain.Privacy.DataExport> DataExports => Set<Domain.Privacy.DataExport>();
    public DbSet<Domain.Onboarding.OnboardingSkip> OnboardingSkips => Set<Domain.Onboarding.OnboardingSkip>();

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
        VehicleMapping.Configure(modelBuilder, VehicleMapping.RefSchema);
        // Busca da vitrine sem diferenciar acentos (RF13 CA3). Extensão confiável: o dono do banco pode criá-la.
        modelBuilder.HasPostgresExtension("unaccent");

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
