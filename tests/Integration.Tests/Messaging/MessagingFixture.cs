using Ecommerce.Domain.Catalog;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Ecommerce.Integration.Tests.Messaging;

/// <summary>Host com Wolverine rodando como app_user (sem DDL) sobre o ambiente de produção simulado (ADR-0002).</summary>
public sealed class MessagingFixture : IAsyncLifetime
{
    private PostgresEnvironment _env = null!;

    public Guid TenantA { get; } = Guid.CreateVersion7();
    public Guid TenantB { get; } = Guid.CreateVersion7();
    public Guid PartA { get; private set; }
    public Guid PartB { get; private set; }

    public IHost Host { get; private set; } = null!;
    public HandlerProbe Probe => Host.Services.GetRequiredService<HandlerProbe>();

    public async Task InitializeAsync()
    {
        _env = await PostgresEnvironment.StartAsync();
        Host = PostgresEnvironment.BuildHost(
            _env.AppConfiguration(), _env.TenantsConnectionString,
            services: s => s.AddSingleton<HandlerProbe>());
        await Host.StartAsync();

        PartA = await SeedAsync(TenantA, "A-001");
        PartB = await SeedAsync(TenantB, "B-001");
    }

    public async Task DisposeAsync()
    {
        if (Host is not null)
        {
            await Host.StopAsync();
            Host.Dispose();
        }
        await _env.DisposeAsync();
    }

    private async Task<Guid> SeedAsync(Guid tenantId, string code)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        var part = new Part(tenantId, code, "Peça " + code, 100m);
        db.Parts.Add(part);
        await db.SaveChangesAsync();
        return part.Id;
    }
}
