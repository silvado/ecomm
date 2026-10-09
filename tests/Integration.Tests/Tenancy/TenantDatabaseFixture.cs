using Ecommerce.Domain.Catalog;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;

namespace Ecommerce.Integration.Tests.Tenancy;

/// <summary>Banco de tenant com dois tenants e peças de cada um.</summary>
public sealed class TenantDatabaseFixture : IAsyncLifetime
{
    private PostgresEnvironment _env = null!;

    public Guid TenantA { get; } = Guid.CreateVersion7();
    public Guid TenantB { get; } = Guid.CreateVersion7();

    /// <summary>Conexão do app_user, com pool de 1 conexão para forçar reaproveitamento.</summary>
    public string AppConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        _env = await PostgresEnvironment.StartAsync();
        AppConnectionString = PostgresEnvironment.With(_env.TenantsConnectionString, b => b.MaxPoolSize = 1);

        await SeedAsync(TenantA, "A-001", "Farol Gol G5 esquerdo");
        await SeedAsync(TenantA, "A-002", "Retrovisor Onix direito");
        await SeedAsync(TenantB, "B-001", "Para-choque Civic 2015");
    }

    public async Task DisposeAsync() => await _env.DisposeAsync();

    public TenantDbContext CreateAppContext(Guid? tenantId)
    {
        var scope = new TenantScope();
        if (tenantId is { } id) scope.Set(id);
        return CreateAppContext(scope);
    }

    /// <summary>Contexto cujo tenant o teste controla; útil para definir o tenant depois de abrir a conexão.</summary>
    public TenantDbContext CreateAppContext(TenantScope scope) =>
        PostgresEnvironment.CreateTenantContext(AppConnectionString, scope);

    private async Task SeedAsync(Guid tenantId, string code, string title)
    {
        await using var context = CreateAppContext(tenantId);
        context.Parts.Add(TestParts.New(tenantId, code, title));
        await context.SaveChangesAsync();
    }
}
