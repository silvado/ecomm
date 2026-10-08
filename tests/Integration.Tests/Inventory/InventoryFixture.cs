using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Inventory;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Wolverine;

namespace Ecommerce.Integration.Tests.Inventory;

/// <summary>Host com Wolverine + banco real para exercitar os handlers de estoque como em produção.</summary>
public sealed class InventoryFixture : IAsyncLifetime
{
    private PostgresEnvironment _env = null!;

    public Guid TenantA { get; } = Guid.CreateVersion7();
    public Guid TenantB { get; } = Guid.CreateVersion7();
    public IHost Host { get; private set; } = null!;
    public string TenantsConnectionString => _env.TenantsConnectionString;

    public async Task InitializeAsync()
    {
        _env = await PostgresEnvironment.StartAsync();
        Host = PostgresEnvironment.BuildHost(_env.AppConfiguration(), _env.TenantsConnectionString);
        await Host.StartAsync();
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

    /// <summary>Cada teste cria a própria peça para não depender da ordem de execução.</summary>
    public async Task<Guid> CreatePartAsync(Guid tenantId, int onHand)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
        var part = new Part(tenantId, $"P-{Guid.CreateVersion7():N}", "Peça de teste", 100m);
        db.Parts.Add(part);
        db.Stocks.Add(new Stock(tenantId, part.Id, onHand));
        await db.SaveChangesAsync();
        return part.Id;
    }

    /// <summary>Envia o comando como a API/handlers fazem: com o tenant no envelope.</summary>
    public async Task<T> InvokeAsync<T>(Guid tenantId, object command)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        return await bus.InvokeForTenantAsync<T>(tenantId.ToString(), command);
    }

    public async Task InvokeAsync(Guid tenantId, object command)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        var bus = scope.ServiceProvider.GetRequiredService<IMessageBus>();
        await bus.InvokeForTenantAsync(tenantId.ToString(), command);
    }

    public async Task<T> QueryAsync<T>(Guid tenantId, Func<TenantDbContext, Task<T>> query)
    {
        await using var scope = Host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await query(scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    public Task<Stock> GetStockAsync(Guid tenantId, Guid partId) =>
        QueryAsync(tenantId, db => db.Stocks.AsNoTracking().SingleAsync(s => s.PartId == partId));

    public Task<StockReservation> GetReservationAsync(Guid tenantId, Guid reservationId) =>
        QueryAsync(tenantId, db => db.StockReservations.AsNoTracking().SingleAsync(r => r.Id == reservationId));

    public Task<int> ExecuteAsync(Guid tenantId, FormattableString sql) =>
        QueryAsync(tenantId, db => db.Database.ExecuteSqlAsync(sql));
}
