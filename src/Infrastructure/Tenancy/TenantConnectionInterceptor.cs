using System.Data.Common;
using Ecommerce.Application.Tenancy;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Ecommerce.Infrastructure.Tenancy;

/// <summary>
/// Fixa <c>app.tenant_id</c> em toda conexão aberta pelo EF Core; as políticas RLS leem essa variável (ADR-0001).
/// Sem tenant, grava string vazia: a política converte em NULL e nenhuma linha passa.
/// O reset do pool do Npgsql (DISCARD ALL) limpa a variável quando a conexão volta ao pool.
/// </summary>
public sealed class TenantConnectionInterceptor(ITenantContext tenant) : DbConnectionInterceptor
{
    public const string SettingName = "app.tenant_id";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        await using var command = CreateCommand(connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        var command = connection.CreateCommand();
        command.CommandText = $"SELECT set_config('{SettingName}', @tenant, false)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "tenant";
        parameter.Value = tenant.TenantId?.ToString() ?? string.Empty;
        command.Parameters.Add(parameter);
        return command;
    }
}
