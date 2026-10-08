using System.Net;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Platform;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>API real sobre PostgreSQL real, com tenants semeados no catálogo.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Cabeçalho só de teste: simula o IP de origem da conexão (o TestServer não tem um).</summary>
    public const string RemoteIpHeader = "X-Test-Remote-Ip";
    public const string ProxyNetwork = "10.0.0.0/8";

    private PostgresEnvironment _env = null!;

    public Tenant Active { get; private set; } = null!;
    public Tenant Suspended { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _env = await PostgresEnvironment.StartAsync();

        var now = DateTimeOffset.UtcNow;
        Active = Tenant.Create("ativa", Cnpj.Parse("11222333000181"), "Ativa Ltda", "Loja Ativa", "plataforma.test", now);
        Active.Activate();
        Suspended = Tenant.Create("suspensa", Cnpj.Parse("12ABC34501DE35"), "Suspensa Ltda", "Loja Suspensa", "plataforma.test", now);
        Suspended.Suspend();

        await using var scope = Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        db.Tenants.AddRange(Active, Suspended);
        await db.SaveChangesAsync();
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        await _env.DisposeAsync();
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        foreach (var (key, value) in _env.AppConfiguration())
            builder.UseSetting(key, value);
        builder.UseSetting("Network:Internal:0", ProxyNetwork);
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>());
    }

    public HttpClient ClientFor(string host, string remoteIp = "203.0.113.10")
    {
        var client = CreateClient();
        client.DefaultRequestHeaders.Host = host;
        client.DefaultRequestHeaders.Add(RemoteIpHeader, remoteIp);
        return client;
    }

    private sealed class FakeRemoteIpStartupFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((http, nextMiddleware) =>
            {
                if (http.Request.Headers.TryGetValue(RemoteIpHeader, out var ip))
                    http.Connection.RemoteIpAddress = IPAddress.Parse(ip.ToString());
                return nextMiddleware(http);
            });
            next(app);
        };
    }
}
