using Ecommerce.Integration.Tests.Infrastructure;
using Npgsql;

namespace Ecommerce.Integration.Tests.Deployment;

/// <summary>O migrador roda a cada deploy: precisa ser idempotente e deixar o app_user funcional sem DDL.</summary>
public sealed class MigratorTests : IAsyncLifetime
{
    private PostgresEnvironment _env = null!;

    public async Task InitializeAsync() => _env = await PostgresEnvironment.StartAsync();

    public async Task DisposeAsync() => await _env.DisposeAsync();

    [Fact]
    public async Task Rodar_o_migrador_de_novo_nao_falha_nem_perde_permissoes()
    {
        await _env.RunMigratorAsync();

        await using var connection = new NpgsqlConnection(_env.TenantsConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT count(*) FROM wolverine.wolverine_incoming_envelopes", connection);
        Assert.NotNull(await command.ExecuteScalarAsync());
    }

    [Fact]
    public async Task App_user_nao_consegue_alterar_schema()
    {
        await using var connection = new NpgsqlConnection(_env.TenantsConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("CREATE TABLE public.intrusa (id int)", connection);

        var ex = await Assert.ThrowsAsync<PostgresException>(() => command.ExecuteNonQueryAsync());
        Assert.Equal(PostgresErrorCodes.InsufficientPrivilege, ex.SqlState);
    }
}
