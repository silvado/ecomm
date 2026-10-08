using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Ecommerce.Infrastructure.Platform;

/// <summary>Usado só pelo <c>dotnet ef</c> para gerar migrações.</summary>
internal sealed class DesignTimePlatformDbContextFactory : IDesignTimeDbContextFactory<PlatformDbContext>
{
    public PlatformDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PlatformDbContext>();
        DependencyInjection.ConfigurePlatformDb(options, "Host=localhost;Database=plataforma;Username=app_migrator");
        return new PlatformDbContext(options.Options);
    }
}
