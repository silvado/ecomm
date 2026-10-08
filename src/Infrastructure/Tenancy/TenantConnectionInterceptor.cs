using System.Data.Common;
using Ecommerce.Application.Tenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ecommerce.Infrastructure.Tenancy;

/// <summary>
/// Mantém <c>app.tenant_id</c> da conexão igual ao tenant do escopo; as políticas RLS leem essa variável (ADR-0001).
/// <list type="bullet">
/// <item>Aplica ao abrir a conexão.</item>
/// <item>Antes de cada comando, reaplica se o tenant do escopo mudou depois da abertura — acontece quando um
/// framework (ex.: transação do Wolverine) abre a conexão antes de o middleware fixar o tenant (ADR-0002).</item>
/// </list>
/// Sem tenant, grava string vazia: a política converte em NULL e nenhuma linha passa (falha fechada).
/// O reset do pool do Npgsql (DISCARD ALL) limpa a variável quando a conexão volta ao pool.
/// Escopo: uma instância por escopo de DI, como o DbContext.
/// </summary>
public sealed class TenantConnectionInterceptor(ITenantContext tenant) : DbCommandInterceptor, IDbConnectionInterceptor
{
    public const string SettingName = "app.tenant_id";

    private DbConnection? _connection;
    private string? _appliedTenant;

    private string CurrentTenant => tenant.TenantId?.ToString() ?? string.Empty;

    public void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData) => Apply(connection);

    public Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default) =>
        ApplyAsync(connection, cancellationToken);

    public override InterceptionResult<DbDataReader> ReaderExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        EnsureApplied(command.Connection);
        return result;
    }

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
    {
        await EnsureAppliedAsync(command.Connection, cancellationToken);
        return result;
    }

    public override InterceptionResult<int> NonQueryExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        EnsureApplied(command.Connection);
        return result;
    }

    public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        await EnsureAppliedAsync(command.Connection, cancellationToken);
        return result;
    }

    public override InterceptionResult<object> ScalarExecuting(DbCommand command, CommandEventData eventData, InterceptionResult<object> result)
    {
        EnsureApplied(command.Connection);
        return result;
    }

    public override async ValueTask<InterceptionResult<object>> ScalarExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<object> result, CancellationToken cancellationToken = default)
    {
        await EnsureAppliedAsync(command.Connection, cancellationToken);
        return result;
    }

    private bool IsCurrent(DbConnection? connection) =>
        connection is null || (ReferenceEquals(connection, _connection) && _appliedTenant == CurrentTenant);

    private void EnsureApplied(DbConnection? connection)
    {
        if (!IsCurrent(connection)) Apply(connection!);
    }

    private async Task EnsureAppliedAsync(DbConnection? connection, CancellationToken ct)
    {
        if (!IsCurrent(connection)) await ApplyAsync(connection!, ct);
    }

    private void Apply(DbConnection connection)
    {
        var value = CurrentTenant;
        using var command = CreateCommand(connection, value);
        command.ExecuteNonQuery();
        (_connection, _appliedTenant) = (connection, value);
    }

    private async Task ApplyAsync(DbConnection connection, CancellationToken ct)
    {
        var value = CurrentTenant;
        await using var command = CreateCommand(connection, value);
        await command.ExecuteNonQueryAsync(ct);
        (_connection, _appliedTenant) = (connection, value);
    }

    private static DbCommand CreateCommand(DbConnection connection, string value)
    {
        // Comando cru sobre a conexão: não passa pelos interceptores do EF (sem recursão).
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT set_config('{SettingName}', @tenant, false)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant";
        parameter.Value = value;
        command.Parameters.Add(parameter);
        return command;
    }
}
