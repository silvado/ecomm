using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Platform;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecommerce.Integration.Tests.Platform;

public sealed class TenantCatalogFixture : IAsyncLifetime
{
    private PostgresEnvironment _env = null!;

    public IHost Host { get; private set; } = null!;
    public Tenant Active { get; private set; } = null!;
    public Tenant Suspended { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _env = await PostgresEnvironment.StartAsync();
        Host = PostgresEnvironment.BuildHost(_env.AppConfiguration(), _env.TenantsConnectionString);

        var now = DateTimeOffset.UtcNow;
        Active = Tenant.Create("ativa", Cnpj.Parse("11222333000181"), "Ativa Ltda", "Loja Ativa", "plataforma.test", now);
        Active.Activate();
        Suspended = Tenant.Create("suspensa", Cnpj.Parse("12ABC34501DE35"), "Suspensa Ltda", "Loja Suspensa", "plataforma.test", now);
        Suspended.Suspend();

        await using var scope = Host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Tenants.AddRange(Active, Suspended);
        await db.SaveChangesAsync();
    }

    public async Task DisposeAsync()
    {
        Host?.Dispose();
        await _env.DisposeAsync();
    }
}

public sealed class TenantCatalogTests(TenantCatalogFixture fx) : IClassFixture<TenantCatalogFixture>
{
    private ITenantCatalog Catalog => fx.Host.Services.GetRequiredService<ITenantCatalog>();

    [Theory]
    [InlineData("ativa.plataforma.test")]
    [InlineData("ATIVA.plataforma.test:443")]
    public async Task Encontra_tenant_pelo_subdominio_normalizado(string host)
    {
        var tenant = await Catalog.FindByHostAsync(host);

        Assert.NotNull(tenant);
        Assert.Equal(fx.Active.Id, tenant.Id);
        Assert.Equal("Loja Ativa", tenant.TradeName);
        Assert.True(tenant.IsStorefrontAvailable);
        Assert.Equal(Tenant.SharedDatabase, tenant.DatabaseKey);
    }

    [Fact]
    public async Task Host_desconhecido_retorna_nulo()
    {
        Assert.Null(await Catalog.FindByHostAsync("nao-existe.plataforma.test"));
        Assert.Null(await Catalog.FindByHostAsync(""));
    }

    [Fact]
    public async Task Tenant_suspenso_e_encontrado_mas_indisponivel()
    {
        var tenant = await Catalog.FindByHostAsync("suspensa.plataforma.test");

        Assert.NotNull(tenant);
        Assert.False(tenant.IsStorefrontAvailable);
    }

    [Fact]
    public async Task Encontra_tenant_pelo_id()
    {
        var tenant = await Catalog.FindByIdAsync(fx.Suspended.Id);

        Assert.NotNull(tenant);
        Assert.Equal("suspensa", tenant.Slug);
        Assert.Equal(TenantStatus.Suspended, tenant.Status);
    }
}
