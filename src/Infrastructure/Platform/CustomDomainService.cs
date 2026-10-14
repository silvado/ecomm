using System.Collections.Concurrent;
using DnsClient;
using Ecommerce.Application.Platform;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Store;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Ecommerce.Infrastructure.Platform;

/// <summary>DNS real (DnsClient, resolvedores do sistema). Erro de consulta conta como "nada encontrado".</summary>
public sealed class DnsClientResolver : IDnsResolver
{
    private static readonly LookupClient Client = new(new LookupClientOptions { UseCache = false, Timeout = TimeSpan.FromSeconds(5), Retries = 1 });

    public Task<IReadOnlyList<string>> CnamesAsync(string host, CancellationToken ct = default) =>
        QueryAsync(host, QueryType.CNAME, r => r.Answers.CnameRecords().Select(c => c.CanonicalName.Value), ct);

    public Task<IReadOnlyList<string>> AddressesAsync(string host, CancellationToken ct = default) =>
        QueryAsync(host, QueryType.A, r => r.Answers.ARecords().Select(a => a.Address.ToString()), ct);

    public Task<IReadOnlyList<string>> TextsAsync(string host, CancellationToken ct = default) =>
        QueryAsync(host, QueryType.TXT, r => r.Answers.TxtRecords().SelectMany(t => t.Text), ct);

    private static async Task<IReadOnlyList<string>> QueryAsync(string host, QueryType type, Func<IDnsQueryResponse, IEnumerable<string>> select, CancellationToken ct)
    {
        try
        {
            var response = await Client.QueryAsync(host, type, cancellationToken: ct);
            return response.HasError ? [] : select(response).Select(v => v.TrimEnd('.').ToLowerInvariant()).ToList();
        }
        catch (DnsResponseException)
        {
            return [];
        }
    }
}

/// <summary>DNS simulado (dev e testes): registros em memória do processo, definidos pelos testes ou pelo endpoint de dev.</summary>
public sealed class FakeDnsResolver : IDnsResolver
{
    private static readonly ConcurrentDictionary<(string Host, string Type), string[]> Records = new();

    public static void Set(string host, string type, params string[] values) =>
        Records[(TenantDomain.NormalizeHost(host), type.ToUpperInvariant())] = values.Select(v => v.TrimEnd('.').ToLowerInvariant()).ToArray();

    public Task<IReadOnlyList<string>> CnamesAsync(string host, CancellationToken ct = default) => Get(host, "CNAME");

    public Task<IReadOnlyList<string>> AddressesAsync(string host, CancellationToken ct = default) => Get(host, "A");

    public Task<IReadOnlyList<string>> TextsAsync(string host, CancellationToken ct = default) => Get(host, "TXT");

    private static Task<IReadOnlyList<string>> Get(string host, string type) =>
        Task.FromResult<IReadOnlyList<string>>(Records.TryGetValue((TenantDomain.NormalizeHost(host), type), out var values) ? values : []);
}

/// <summary>
/// Confere um domínio próprio (RF03 CA2, ADR-0004): aponta para a plataforma (CNAME para o subdomínio da loja ou A para um
/// IP da plataforma) e tem o TXT de verificação com o código da loja (prova de controle — decidido em 13/10/2026).
/// </summary>
public sealed class DomainVerifier(IDnsResolver dns, IOptions<PlatformOptions> platform)
{
    public async Task<(bool Ok, string? Problem)> CheckAsync(TenantDomain domain, string slug, CancellationToken ct)
    {
        var target = $"{slug}.{platform.Value.Domain}".ToLowerInvariant();
        var cnames = await dns.CnamesAsync(domain.Host, ct);
        var addresses = await dns.AddressesAsync(domain.Host, ct);
        var publicIps = platform.Value.PublicIps.Select(ip => ip.Trim()).ToHashSet(StringComparer.Ordinal);
        var points = cnames.Contains(target) || addresses.Any(publicIps.Contains);
        var texts = await dns.TextsAsync(domain.VerificationRecordName, ct);
        var proven = texts.Contains(domain.VerificationRecordValue.ToLowerInvariant());

        if (points && proven) return (true, null);
        if (!points)
            return (false, cnames.Count > 0 ? $"O {domain.Host} aponta para {cnames[0]}, não para {target}."
                : addresses.Count > 0 ? $"O {domain.Host} aponta para {addresses[0]}, que não é da plataforma."
                : $"Ainda não encontramos o registro de {domain.Host}. A propagação pode levar algumas horas.");
        return (false, $"Falta o registro TXT {domain.VerificationRecordName} com o código da loja.");
    }

    /// <summary>Registros que a loja cria (RF03 CA1).</summary>
    public IReadOnlyList<DnsInstruction> Instructions(TenantDomain domain, string slug)
    {
        var target = $"{slug}.{platform.Value.Domain}";
        var records = new List<DnsInstruction>();
        if (domain.Kind == DomainKind.CustomWww)
            records.Add(new("CNAME", domain.Host, target, "Aponta o www para a sua loja na plataforma."));
        else
        {
            records.Add(new("ALIAS / ANAME", domain.Host, target, "Se o seu provedor de DNS tiver ALIAS, ANAME ou \"CNAME flattening\" no domínio raiz (ou CNAME, se for um subdomínio)."));
            foreach (var ip in platform.Value.PublicIps)
                records.Add(new("A", domain.Host, ip, "Se não tiver ALIAS: registro A para o IP da plataforma.", Alternative: true));
        }
        records.Add(new("TXT", domain.VerificationRecordName, domain.VerificationRecordValue, "Prova que o domínio é da loja. Não mexe no seu e-mail."));
        return records;
    }
}

/// <summary>Domínio próprio da loja do escopo, no painel (RF03; recurso do plano — RF37).</summary>
public sealed class CustomDomainService(PlatformDbContext platform, ITenantContext tenant, PlanDirectory plans, DomainVerifier verifier,
    ITenantCatalog catalog, IMemoryCache cache, IOptions<PlatformOptions> options, TimeProvider clock) : ICustomDomains
{
    public async Task<CustomDomainsView> ListAsync(CancellationToken ct = default)
    {
        var row = await TenantAsync(ct);
        var plan = await plans.ForTenantAsync(row.Id, ct);
        return View(row, plan.Has(PlanFeature.CustomDomain));
    }

    public async Task<CustomDomainsView> AddAsync(string? host, CancellationToken ct = default)
    {
        var row = await TenantAsync(ct);
        var plan = await plans.ForTenantAsync(row.Id, ct);
        if (!plan.Has(PlanFeature.CustomDomain)) throw new InvalidOperationException(PlanDirectory.FeatureMessage(plan, "domínio próprio"));
        var (normalized, kind) = TenantDomain.ParseCustom(host, options.Value.Domain);
        if (await platform.TenantDomains.AnyAsync(d => d.Host == normalized && d.TenantId != row.Id, ct))
            throw new InvalidOperationException("Este domínio já está cadastrado em outra loja da plataforma.");
        row.AddCustomDomain(normalized, kind, clock.GetUtcNow());
        try
        {
            await platform.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new InvalidOperationException("Este domínio já está cadastrado em outra loja da plataforma.", e);
        }
        return View(row, true);
    }

    public async Task<CustomDomainsView?> RemoveAsync(Guid domainId, CancellationToken ct = default)
    {
        var row = await TenantAsync(ct);
        if (row.Domains.SingleOrDefault(d => d.Id == domainId && d.IsCustom) is not { } removed) return null;
        row.RemoveCustomDomain(domainId);
        await platform.SaveChangesAsync(ct);
        Forget(row.Id, removed.Host);
        return View(row, (await plans.ForTenantAsync(row.Id, ct)).Has(PlanFeature.CustomDomain));
    }

    public async Task<CustomDomainsView?> VerifyNowAsync(Guid domainId, CancellationToken ct = default)
    {
        var row = await TenantAsync(ct);
        var domain = row.Domains.SingleOrDefault(d => d.Id == domainId && d.IsCustom);
        if (domain is null) return null;
        var now = clock.GetUtcNow();
        row.RestartDomainVerification(domainId, now);
        await DomainVerificationJob.ApplyAsync(row, domain, verifier, now, ct);
        await platform.SaveChangesAsync(ct);
        Forget(row.Id, domain.Host);
        return View(row, (await plans.ForTenantAsync(row.Id, ct)).Has(PlanFeature.CustomDomain));
    }

    /// <summary>
    /// O host principal está no catálogo e no <see cref="Ecommerce.Application.Store.PublicStore"/> em cache: sem limpar os
    /// dois, o subdomínio continuaria redirecionando para um domínio removido até o cache vencer.
    /// </summary>
    private void Forget(Guid tenantId, string host)
    {
        catalog.Invalidate(tenantId);
        catalog.InvalidateHost(host);
        cache.Remove(StoreProfileService.PublicKey(tenantId));
    }

    private async Task<Tenant> TenantAsync(CancellationToken ct)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Domínio próprio sem loja no escopo.");
        return await platform.Tenants.Include(t => t.Domains).SingleAsync(t => t.Id == tenantId, ct);
    }

    private CustomDomainsView View(Tenant row, bool allowed) => new(allowed,
        row.Domains.Single(d => d.Kind == DomainKind.PlatformSubdomain).Host,
        row.Domains.Where(d => d.IsCustom).OrderBy(d => d.Kind).Select(d => new CustomDomainView(d.Id, d.Host, d.Kind, d.VerificationStatus, d.IsPrimary,
            d.RequestedAt, d.LastCheckedAt, d.LastCheckMessage, d.VerifiedAt, verifier.Instructions(d, row.Slug))).ToList());
}

/// <summary>
/// Verificação dos domínios próprios pendentes (RF03 CA2): a cada 5 minutos, por até 72 horas. Verificado, o domínio
/// passa a abrir a loja e entra na lista do Caddy (endpoint ask). Roda só no Worker.
/// </summary>
public sealed partial class DomainVerificationJob(IServiceScopeFactory scopes, TimeProvider clock, ILogger<DomainVerificationJob> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                await Task.Delay(Interval, clock, stoppingToken);
                await CheckPendingAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Desligando.
        }
    }

    /// <summary>Confere todos os pendentes; devolve quantos ficaram verificados.</summary>
    public async Task<int> CheckPendingAsync(CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var platform = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        var verifier = scope.ServiceProvider.GetRequiredService<DomainVerifier>();
        var tenants = await platform.Tenants.Include(t => t.Domains)
            .Where(t => t.Domains.Any(d => d.VerificationStatus == DomainVerificationStatus.Pending)).ToListAsync(ct);
        var verified = 0;
        var now = clock.GetUtcNow();
        foreach (var row in tenants)
            foreach (var domain in row.Domains.Where(d => d.VerificationStatus == DomainVerificationStatus.Pending).ToList())
            {
                try
                {
                    if (await ApplyAsync(row, domain, verifier, now, ct)) verified++;
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    LogCheckFailed(logger, e, domain.Host);
                }
            }
        await platform.SaveChangesAsync(ct);
        if (verified > 0) LogVerified(logger, verified);
        return verified;
    }

    internal static async Task<bool> ApplyAsync(Tenant row, TenantDomain domain, DomainVerifier verifier, DateTimeOffset now, CancellationToken ct)
    {
        var (ok, problem) = await verifier.CheckAsync(domain, row.Slug, ct);
        if (ok) row.DomainVerified(domain.Id, now);
        else row.DomainCheckFailed(domain.Id, problem!, now);
        return ok;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Domínios próprios verificados: {Count}")]
    private static partial void LogVerified(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Falha ao verificar o domínio {Host}")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception, string host);
}
