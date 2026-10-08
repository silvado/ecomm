using Ecommerce.Infrastructure.Tenancy;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecommerce.Infrastructure.Persistence;

/// <summary>Usado só pelo <c>dotnet ef</c> para gerar migrações.</summary>
internal sealed class DesignTimeTenantDbContextFactory : IDesignTimeDbContextFactory<TenantDbContext>
{
    public TenantDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<TenantDbContext>();
        DependencyInjection.ConfigureTenantDb(options, "Host=localhost;Database=lojas;Username=app_migrator");
        return new TenantDbContext(options.Options, new TenantContext());
    }
}
