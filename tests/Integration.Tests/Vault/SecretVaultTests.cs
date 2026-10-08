using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Vault;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Ecommerce.Infrastructure.Vault;
using Ecommerce.Integration.Tests.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecommerce.Integration.Tests.Vault;

public sealed class VaultFixture : IAsyncLifetime
{
    public PostgresEnvironment Env { get; private set; } = null!;
    public IHost Host { get; private set; } = null!;
    public CapturingLoggerProvider Logs { get; } = new();

    public async Task InitializeAsync()
    {
        Env = await PostgresEnvironment.StartAsync();
        Host = BuildHost(Env.AppConfiguration());
    }

    public async Task DisposeAsync()
    {
        if (Host is IAsyncDisposable host)
            await host.DisposeAsync();
        await Env.DisposeAsync();
    }

    /// <summary>Host com todos os logs (inclusive Trace do EF) capturados, para provar que nenhum segredo vaza.</summary>
    public IHost BuildHost(Dictionary<string, string?> configuration) =>
        PostgresEnvironment.BuildHost(configuration, Env.TenantsConnectionString, services: s =>
        {
            s.AddSingleton<ILoggerProvider>(Logs);
            s.Configure<LoggerFilterOptions>(o => o.MinLevel = LogLevel.Trace);
        });

    public static async Task<T> InTenantAsync<T>(IHost host, Guid tenantId, Func<SecretVault, TenantDbContext, Task<T>> action)
    {
        await using var scope = host.Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
        return await action(scope.ServiceProvider.GetRequiredService<SecretVault>(), scope.ServiceProvider.GetRequiredService<TenantDbContext>());
    }

    public Task<T> InTenantAsync<T>(Guid tenantId, Func<SecretVault, TenantDbContext, Task<T>> action) => InTenantAsync(Host, tenantId, action);
}

public sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentQueue<string> _messages = new();

    public string AllText => string.Join('\n', _messages);

    public ILogger CreateLogger(string categoryName) => new Logger(_messages);

    public void Dispose() { }

    private sealed class Logger(ConcurrentQueue<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Enqueue(formatter(state, exception) + exception);
    }
}

/// <summary>RF06 — cofre de segredos por tenant.</summary>
public sealed class SecretVaultTests(VaultFixture fx) : IClassFixture<VaultFixture>
{
    private static ReadOnlyMemory<byte> Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static string NewToken() => "APP_USR-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    [Fact]
    public async Task Grava_e_le_segredo_e_listagem_mostra_so_metadados()
    {
        var tenant = Guid.CreateVersion7();
        var token = NewToken();

        await fx.InTenantAsync(tenant, (v, _) => v.SetAsync(SecretKind.PaymentGatewayCredential, "mercado-pago:access-token", Utf8(token)));

        var read = await fx.InTenantAsync(tenant, (v, _) => v.GetAsync(SecretKind.PaymentGatewayCredential, "mercado-pago:access-token"));
        Assert.Equal(token, Encoding.UTF8.GetString(read!));

        var metadata = Assert.Single(await fx.InTenantAsync(tenant, (v, _) => v.ListAsync()));
        Assert.Equal(token[^4..], metadata.Hint);
        Assert.DoesNotContain(token, metadata.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Banco_guarda_somente_texto_cifrado()
    {
        var tenant = Guid.CreateVersion7();
        var token = NewToken();
        await fx.InTenantAsync(tenant, (v, _) => v.SetAsync(SecretKind.ApiKey, "melhor-envio", Utf8(token)));

        var stored = await fx.InTenantAsync(tenant, (_, db) => db.TenantSecrets.AsNoTracking().SingleAsync());

        Assert.DoesNotContain(token, Encoding.UTF8.GetString(stored.Ciphertext), StringComparison.Ordinal);
        Assert.DoesNotContain(token, Convert.ToBase64String(stored.Ciphertext), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Outro_tenant_nao_le_o_segredo()
    {
        var (a, b) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await fx.InTenantAsync(a, (v, _) => v.SetAsync(SecretKind.ApiKey, "chave", Utf8(NewToken())));

        Assert.Null(await fx.InTenantAsync(b, (v, _) => v.GetAsync(SecretKind.ApiKey, "chave")));
        Assert.Empty(await fx.InTenantAsync(b, (v, _) => v.ListAsync()));
    }

    [Fact]
    public async Task Texto_cifrado_copiado_para_outro_tenant_nao_decifra()
    {
        var (a, b) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await fx.InTenantAsync(a, (v, _) => v.SetAsync(SecretKind.ApiKey, "chave", Utf8(NewToken())));
        await fx.InTenantAsync(b, (v, _) => v.SetAsync(SecretKind.ApiKey, "outra", Utf8(NewToken()))); // B já tem DEK
        var stolen = await fx.InTenantAsync(a, (_, db) => db.TenantSecrets.AsNoTracking().SingleAsync());

        // Simula alguém com acesso ao banco copiando a linha de A para B.
        await fx.InTenantAsync(b, (_, db) => db.Database.ExecuteSqlAsync($"""
            INSERT INTO tenant_secrets (id, tenant_id, kind, name, ciphertext, key_version, hint, created_at, updated_at)
            VALUES ({Guid.CreateVersion7()}, {b}, 'ApiKey', 'chave', {stolen.Ciphertext}, '1', '', now(), now())
            """));

        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            fx.InTenantAsync(b, (v, _) => v.GetAsync(SecretKind.ApiKey, "chave")));
    }

    [Fact]
    public async Task Substituir_segredo_mantem_um_registro_com_o_valor_novo()
    {
        var tenant = Guid.CreateVersion7();
        await fx.InTenantAsync(tenant, (v, _) => v.SetAsync(SecretKind.ApiKey, "chave", Utf8(NewToken())));
        var newer = NewToken();

        await fx.InTenantAsync(tenant, (v, _) => v.SetAsync(SecretKind.ApiKey, "chave", Utf8(newer)));

        Assert.Equal(newer, Encoding.UTF8.GetString((await fx.InTenantAsync(tenant, (v, _) => v.GetAsync(SecretKind.ApiKey, "chave")))!));
        Assert.Single(await fx.InTenantAsync(tenant, (v, _) => v.ListAsync()));
    }

    [Fact]
    public async Task Rotacao_da_chave_mestra_recifra_a_DEK_e_dispensa_a_chave_antiga()
    {
        var tenant = Guid.CreateVersion7();
        var token = NewToken();
        await fx.InTenantAsync(tenant, (v, _) => v.SetAsync(SecretKind.ApiKey, "chave", Utf8(token)));

        var v2 = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var both = fx.Env.AppConfiguration();
        both["Vault:MasterKeys:v2"] = v2;
        both["Vault:CurrentMasterKeyVersion"] = "v2";
        using (var rotating = fx.BuildHost(both))
            Assert.True(await VaultFixture.InTenantAsync(rotating, tenant, (v, _) => v.RewrapDataKeyAsync()));

        var onlyV2 = fx.Env.AppConfiguration();
        onlyV2.Remove("Vault:MasterKeys:v1");
        onlyV2["Vault:MasterKeys:v2"] = v2;
        onlyV2["Vault:CurrentMasterKeyVersion"] = "v2";
        using var afterRotation = fx.BuildHost(onlyV2);
        var read = await VaultFixture.InTenantAsync(afterRotation, tenant, (v, _) => v.GetAsync(SecretKind.ApiKey, "chave"));

        Assert.Equal(token, Encoding.UTF8.GetString(read!));
    }

    [Fact]
    public async Task Primeiro_uso_concorrente_cria_uma_unica_chave_de_dados()
    {
        var tenant = Guid.CreateVersion7();

        await Task.WhenAll(Enumerable.Range(0, 10).Select(i => Task.Run(() =>
            fx.InTenantAsync(tenant, (v, _) => v.SetAsync(SecretKind.ApiKey, $"chave-{i}", Utf8(NewToken()))))));

        Assert.Equal(1, await fx.InTenantAsync(tenant, (_, db) => db.TenantDataKeys.CountAsync()));
        Assert.Equal(10, (await fx.InTenantAsync(tenant, (v, _) => v.ListAsync())).Count);
        foreach (var i in Enumerable.Range(0, 10))
            Assert.NotNull(await fx.InTenantAsync(tenant, (v, _) => v.GetAsync(SecretKind.ApiKey, $"chave-{i}")));
    }

    [Fact]
    public async Task Nenhum_segredo_aparece_nos_logs()
    {
        var tenant = Guid.CreateVersion7();
        var token = NewToken();

        await fx.InTenantAsync(tenant, (v, _) => v.SetAsync(SecretKind.OAuthToken, "mercado-livre", Utf8(token)));
        await fx.InTenantAsync(tenant, (v, _) => v.GetAsync(SecretKind.OAuthToken, "mercado-livre"));

        Assert.NotEmpty(fx.Logs.AllText); // os logs estão sendo capturados (EF em Trace)
        Assert.DoesNotContain(token, fx.Logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(token[..12], fx.Logs.AllText, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Le_validade_do_certificado_A1_e_guarda_sem_dica()
    {
        var tenant = Guid.CreateVersion7();
        var notAfter = new DateTimeOffset(2027, 3, 15, 12, 0, 0, TimeSpan.Zero);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=LOJA TESTE LTDA:11222333000181", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(notAfter.AddYears(-1), notAfter);
        var pfx = certificate.Export(X509ContentType.Pfx, "senha-teste");

        var expiry = A1Certificate.ReadExpiry(pfx, "senha-teste");
        var metadata = await fx.InTenantAsync(tenant, (v, _) => v.SetAsync(SecretKind.A1Certificate, "nfe", pfx, expiry));

        Assert.Equal(notAfter, expiry);
        Assert.Equal(expiry, metadata.ExpiresAt);
        Assert.Equal(string.Empty, metadata.Hint);
    }
}
