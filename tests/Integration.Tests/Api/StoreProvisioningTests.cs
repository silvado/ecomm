using System.Net;
using System.Net.Http.Json;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using static Ecommerce.Integration.Tests.Api.PanelHttp;

namespace Ecommerce.Integration.Tests.Api;

/// <summary>RF01/RF02 — criação de loja com subdomínio e Dono.</summary>
public sealed class StoreProvisioningTests(ApiFactory api) : IClassFixture<ApiFactory>
{
    private sealed record StoreDto(string Slug, string Name);

    private static string NewSlug() => "loja" + Guid.NewGuid().ToString("N")[..10];

    private async Task<CreateTenantResult> CreateAsync(string slug, string cnpj, string ownerEmail, string? ownerPassword = null)
    {
        await using var scope = api.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<ITenantProvisioning>().CreateAsync(
            new CreateTenantRequest(slug, cnpj, "Peças Teste Ltda", "Peças Teste", ownerEmail, ownerPassword));
    }

    private static string NewEmail() => $"dono{Guid.NewGuid():N}@pecas.test";

    [Fact]
    public async Task Loja_criada_responde_no_subdominio_e_o_dono_entra_com_a_senha_provisoria()
    {
        var slug = NewSlug();
        var email = NewEmail();

        var result = await CreateAsync(slug, Cnpjs.Random().Value, email);

        var created = result.Tenant!;
        Assert.Equal($"{slug}.plataforma.test", created.Host);
        Assert.True(PasswordPolicy.IsValid(created.TemporaryPassword));

        var store = await api.ClientFor(created.Host).GetFromJsonAsync<StoreDto>("/api/loja/identidade");
        Assert.Equal(new StoreDto(slug, "Peças Teste"), store);
        Assert.Equal(HttpStatusCode.OK, (await api.ClientFor("api:8080", "10.0.0.2").GetAsync($"/internal/tls/ask?domain={created.Host}")).StatusCode);

        var login = await LoginAsync(api.PanelClient(), email, created.TemporaryPassword!);
        Assert.True(login.Session!.MustChangePassword);
        Assert.Equal("owner", login.Session.Tenant!.Role);
    }

    [Fact]
    public async Task Dono_que_ja_tem_conta_so_ganha_o_vinculo()
    {
        var first = (await CreateAsync(NewSlug(), Cnpjs.Random().Value, NewEmail())).Tenant!;

        var second = (await CreateAsync(NewSlug(), Cnpjs.Random().Value, first.OwnerEmail.ToUpperInvariant())).Tenant!;

        Assert.Equal(first.OwnerId, second.OwnerId);
        Assert.Null(second.TemporaryPassword);
        var login = await LoginAsync(api.PanelClient(), first.OwnerEmail, first.TemporaryPassword!);
        Assert.Equal(2, login.Session!.Tenants.Count);
    }

    [Fact]
    public async Task Slug_e_cnpj_sao_unicos_mesmo_com_cnpj_formatado()
    {
        var slug = NewSlug();
        var cnpj = Cnpjs.Random().Value;
        Assert.NotNull((await CreateAsync(slug, cnpj, NewEmail())).Tenant);

        Assert.Equal(CreateTenantError.SlugTaken, (await CreateAsync(slug, Cnpjs.Random().Value, NewEmail())).Error);
        var formatted = $"{cnpj[..2]}.{cnpj[2..5]}.{cnpj[5..8]}/{cnpj[8..12]}-{cnpj[12..]}";
        Assert.Equal(CreateTenantError.CnpjTaken, (await CreateAsync(NewSlug(), formatted, NewEmail())).Error);
    }

    [Theory]
    [InlineData("ab", "11222333000181", "dono@pecas.test", CreateTenantError.InvalidSlug)]
    [InlineData("admin", "11222333000181", "dono@pecas.test", CreateTenantError.InvalidSlug)]
    [InlineData("loja-nova-x", "11222333000182", "dono@pecas.test", CreateTenantError.InvalidCnpj)]
    [InlineData("loja-nova-x", "11222333000181", "sem-arroba", CreateTenantError.InvalidOwnerEmail)]
    public async Task Dados_invalidos_nao_criam_nada(string slug, string cnpj, string email, CreateTenantError expected)
    {
        var before = await api.WithPlatformAsync(db => db.Tenants.CountAsync());

        Assert.Equal(expected, (await CreateAsync(slug, cnpj, email)).Error);

        Assert.Equal(before, await api.WithPlatformAsync(db => db.Tenants.CountAsync()));
    }

    [Fact]
    public async Task Criacoes_simultaneas_com_o_mesmo_slug_criam_uma_loja()
    {
        var slug = NewSlug();

        var results = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => Task.Run(() => CreateAsync(slug, Cnpjs.Random().Value, NewEmail()))));

        Assert.Single(results, r => r.Tenant is not null);
        Assert.All(results.Where(r => r.Tenant is null), r => Assert.Equal(CreateTenantError.SlugTaken, r.Error));
        Assert.Equal(1, await api.WithPlatformAsync(db => db.Tenants.CountAsync(t => t.Slug == slug)));
    }

    [Fact]
    public async Task Senha_provisoria_nao_aparece_nos_logs()
    {
        var created = (await CreateAsync(NewSlug(), Cnpjs.Random().Value, NewEmail())).Tenant!;

        Assert.DoesNotContain(created.TemporaryPassword!, api.Logs.AllText, StringComparison.Ordinal);
        Assert.DoesNotContain(created.TemporaryPassword!, created.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Senha_da_semente_de_dev_dispensa_troca()
    {
        var email = NewEmail();

        var created = (await CreateAsync(NewSlug(), Cnpjs.Random().Value, email, "senha-de-dev-123")).Tenant!;

        Assert.Null(created.TemporaryPassword);
        Assert.False((await LoginAsync(api.PanelClient(), email, "senha-de-dev-123")).Session!.MustChangePassword);
        Assert.Equal(TenantStatus.Active, await api.WithPlatformAsync(db => db.Tenants.Where(t => t.Id == created.TenantId).Select(t => t.Status).SingleAsync()));
    }
}
