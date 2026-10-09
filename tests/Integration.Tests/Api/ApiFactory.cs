using System.Net;
using System.Security.Cryptography;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Identity;
using Ecommerce.Infrastructure.Platform;
using Ecommerce.Integration.Tests.Infrastructure;
using Ecommerce.Integration.Tests.Vault;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>API real sobre PostgreSQL real, com tenants semeados no catálogo.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>Cabeçalho só de teste: simula o IP de origem da conexão (o TestServer não tem um).</summary>
    public const string RemoteIpHeader = "X-Test-Remote-Ip";
    public const string ProxyNetwork = "10.0.0.0/8";
    public const string PanelHost = "api.plataforma.test";

    private PostgresEnvironment _env = null!;

    public Tenant Active { get; private set; } = null!;
    public Tenant Suspended { get; private set; } = null!;
    public string SigningKey { get; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    /// <summary>Todos os logs da API (inclusive Trace), para provar que senhas e tokens não vazam.</summary>
    public CapturingLoggerProvider Logs { get; } = new();

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
        builder.UseSetting("Auth:SigningKey", SigningKey);
        builder.ConfigureLogging(logging => logging.SetMinimumLevel(LogLevel.Trace).AddProvider(Logs));
        builder.ConfigureServices(services => services.AddSingleton<IStartupFilter, FakeRemoteIpStartupFilter>());
    }

    /// <summary>Sem gerenciamento automático de cookies: os testes controlam o cookie de renovação.</summary>
    public HttpClient ClientFor(string host, string? remoteIp = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        client.DefaultRequestHeaders.Host = host;
        client.DefaultRequestHeaders.Add(RemoteIpHeader, remoteIp ?? "203.0.113.10");
        return client;
    }

    /// <summary>Cliente do painel com IP próprio (o limite de login é por IP).</summary>
    public HttpClient PanelClient() =>
        ClientFor(PanelHost, $"198.18.{RandomNumberGenerator.GetInt32(256)}.{RandomNumberGenerator.GetInt32(1, 255)}");

    public async Task<Tenant> CreateTenantAsync(string planCode = Plan.Essential)
    {
        var slug = "t" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant();
        var tenant = Tenant.Create(slug, Cnpjs.Random(), $"{slug} Ltda", $"Loja {slug}", "plataforma.test", DateTimeOffset.UtcNow);
        tenant.Activate();
        tenant.ChangePlan(planCode);
        await WithPlatformAsync(async db =>
        {
            db.Tenants.Add(tenant);
            await db.SaveChangesAsync();
        });
        return tenant;
    }

    public async Task<UserAccount> CreateUserAsync(Guid tenantId, TenantRole role, string password, bool mustChangePassword = false)
    {
        var email = $"u{Convert.ToHexString(RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}@pecas.test";
        var user = UserAccount.Create(email, Services.GetRequiredService<PasswordHashing>().Hash(password), mustChangePassword, DateTimeOffset.UtcNow);
        await WithPlatformAsync(async db =>
        {
            db.UserAccounts.Add(user);
            db.TenantMemberships.Add(new TenantMembership(tenantId, user.Id, role, DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();
        });
        return user;
    }

    public Task AddMembershipAsync(Guid tenantId, Guid userId, TenantRole role) => WithPlatformAsync(async db =>
    {
        db.TenantMemberships.Add(new TenantMembership(tenantId, userId, role, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    });

    public async Task WithPlatformAsync(Func<PlatformDbContext, Task> action)
    {
        await using var scope = Services.CreateAsyncScope();
        await action(scope.ServiceProvider.GetRequiredService<PlatformDbContext>());
    }

    public async Task<T> WithPlatformAsync<T>(Func<PlatformDbContext, Task<T>> action)
    {
        await using var scope = Services.CreateAsyncScope();
        return await action(scope.ServiceProvider.GetRequiredService<PlatformDbContext>());
    }

    public Task SqlAsync(FormattableString sql) => WithPlatformAsync(db => db.Database.ExecuteSqlAsync(sql));

    /// <summary>Importa veículos como o comando do migrador (papel de migração) e replica para ref.*.</summary>
    public Task<Ecommerce.Infrastructure.Vehicles.VehicleImportReport> ImportVehiclesAsync(string csv) => _env.ImportVehiclesAsync(csv);

    public string TenantsConnectionString => _env.TenantsConnectionString;

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

/// <summary>CNPJs numéricos válidos aleatórios (dígitos verificadores calculados), para lojas criadas por teste.</summary>
public static class Cnpjs
{
    public static Cnpj Random()
    {
        var digits = Enumerable.Range(0, 12).Select(_ => RandomNumberGenerator.GetInt32(10)).ToList();
        digits.Add(CheckDigit(digits, [5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]));
        digits.Add(CheckDigit(digits, [6, 5, 4, 3, 2, 9, 8, 7, 6, 5, 4, 3, 2]));
        return Cnpj.Parse(string.Concat(digits));
    }

    private static int CheckDigit(List<int> digits, int[] weights)
    {
        var remainder = digits.Zip(weights, (d, w) => d * w).Sum() % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }
}
