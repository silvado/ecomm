using System.Globalization;
using System.Text.Json.Nodes;
using Ecommerce.Application.Catalog;
using Ecommerce.Application.Channels;
using Ecommerce.Application.Integrations;
using Ecommerce.Domain.Channels;
using Ecommerce.Infrastructure.Integrations;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace Ecommerce.Infrastructure.Channels.MercadoLivre;

/// <summary>
/// Compatibilidades da peça no anúncio do ML (RF21 CA3). Cada compatibilidade da peça (versão + anos) vira, por ano, uma
/// "família" do catálogo de veículos do ML: marca + modelo + ano e, quando a peça não serve em todas as versões do modelo
/// naquele ano, também o motor. Os ids do ML vêm do <c>top_values</c> pelo nome da nossa tabela (em cache por 1 dia); o que
/// não casa com segurança fica de fora (lista "não encontrados") — nunca se envia veículo a mais. Fonte:
/// https://developers.mercadolivre.com.br/pt_br/compatibilidades-itens-e-produtos-de-autopecas e
/// https://developers.mercadolivre.com.br/pt_br/referencias-de-dominios-produtos-e-atributos-para-autopecas
/// </summary>
public sealed class MercadoLivreCompatibilityService(
    TenantDbContext db, MercadoLivreApiClient api, MercadoLivreCredentials credentials, IMemoryCache cache, IIntegrationLog log, TimeProvider clock)
    : IMercadoLivreCompatibilities
{
    /// <summary>Famílias por chamada (limite do ML).</summary>
    public const int FamiliesPerCall = 10;

    /// <summary>HIPÓTESE: até 120 famílias (12 chamadas) por envio — o ML limita a 100 chamadas/minuto por app.</summary>
    public const int MaxFamilies = 120;

    private static readonly TimeSpan CacheFor = TimeSpan.FromDays(1);

    /// <summary>Um veículo do lado de cá, num ano: <see cref="Engine"/> nulo = todas as motorizações do modelo.</summary>
    public sealed record Family(string Brand, string Model, int Year, string? Engine)
    {
        public string Label => $"{Brand} {Model} {Year}{(Engine is null ? string.Empty : " " + Engine)}";
    }

    public async Task<CompatibilityReport> SyncAsync(Guid listingId, CancellationToken ct = default)
    {
        var target = await (from l in db.ChannelListings.AsNoTracking()
                            join c in db.ChannelConnections.AsNoTracking() on l.ConnectionId equals c.Id
                            where l.Id == listingId
                            select new { l.PartId, l.ExternalId, c.Id, c.Channel, c.Status }).SingleOrDefaultAsync(ct)
            ?? throw new InvalidOperationException("Anúncio não encontrado.");
        if (target.Channel != SalesChannel.MercadoLivre || target.ExternalId is null)
            throw new InvalidOperationException("Só anúncio publicado no Mercado Livre recebe compatibilidades.");
        if (target.Status != ChannelConnectionStatus.Active)
            throw new InvalidOperationException("A conexão com o Mercado Livre não está ativa. Confira em Canais de venda.");

        var families = await FamiliesAsync(target.PartId, ct);
        if (families.Count == 0) return new CompatibilityReport(0, []);

        var token = await credentials.AccessTokenAsync(target.Id, ct);
        var mapped = new List<JsonObject>();
        var unmapped = new List<string>();
        foreach (var family in families.Take(MaxFamilies))
        {
            if (await MapAsync(token, family, ct) is { } attributes)
                mapped.Add(new JsonObject { ["domain_id"] = MercadoLivreApiClient.VehicleDomain, ["creation_source"] = "DEFAULT", ["attributes"] = attributes });
            else unmapped.Add(family.Label);
        }
        unmapped.AddRange(families.Skip(MaxFamilies).Select(f => f.Label));

        var sent = 0;
        try
        {
            foreach (var batch in mapped.Chunk(FamiliesPerCall))
            {
                var body = new JsonObject { ["products_families"] = new JsonArray(batch.Select(b => (JsonNode)b).ToArray()) };
                sent += await Logged.RunAsync(log, SalesChannel.MercadoLivre, "enviar compatibilidades",
                    () => api.AddCompatibilitiesAsync(token, target.ExternalId, body, ct), n => $"{target.ExternalId} · {n} veículos", null, target.PartId, ct);
            }
        }
        catch (Exception e) when (e is ChannelValidationException or ChannelConnectorException)
        {
            return new CompatibilityReport(sent, unmapped, $"O Mercado Livre não aceitou parte das compatibilidades: {e.Message}");
        }
        return new CompatibilityReport(sent, unmapped);
    }

    /// <summary>Famílias da peça (ano a ano), sem repetir. Versão sem ano final vai até o ano atual.</summary>
    internal async Task<List<Family>> FamiliesAsync(Guid partId, CancellationToken ct)
    {
        var compatibilities = await (from c in db.PartCompatibilities.AsNoTracking()
                                     join v in db.VehicleVersions.AsNoTracking() on c.VehicleVersionId equals v.Id
                                     join m in db.VehicleModels.AsNoTracking() on v.ModelId equals m.Id
                                     join b in db.VehicleBrands.AsNoTracking() on m.BrandId equals b.Id
                                     where c.PartId == partId
                                     select new { Brand = b.Name, Model = m.Name, ModelId = m.Id, VersionId = v.Id, v.Engine,
                                         From = c.YearFrom ?? v.YearFrom, To = c.YearFrom != null ? c.YearTo : v.YearTo }).ToListAsync(ct);
        if (compatibilities.Count == 0) return [];
        var modelIds = compatibilities.Select(c => c.ModelId).Distinct().ToList();
        var versions = await db.VehicleVersions.AsNoTracking().Where(v => modelIds.Contains(v.ModelId))
            .Select(v => new { v.Id, v.ModelId, v.YearFrom, v.YearTo }).ToListAsync(ct);
        var currentYear = clock.GetUtcNow().Year;

        var families = new List<Family>();
        foreach (var model in compatibilities.GroupBy(c => (c.Brand, c.Model, c.ModelId)))
        {
            var years = model.SelectMany(c => Enumerable.Range(c.From, Math.Max(0, (c.To ?? currentYear) - c.From + 1))).Distinct().Order();
            foreach (var year in years)
            {
                var covering = model.Where(c => c.From <= year && (c.To ?? currentYear) >= year).ToList();
                var allVersions = versions.Where(v => v.ModelId == model.Key.ModelId && v.YearFrom <= year && (v.YearTo ?? currentYear) >= year).Select(v => v.Id);
                if (allVersions.All(id => covering.Any(c => c.VersionId == id)))
                    families.Add(new Family(model.Key.Brand, model.Key.Model, year, null));
                else
                    families.AddRange(covering.Select(c => c.Engine).Distinct().Select(engine => new Family(model.Key.Brand, model.Key.Model, year, engine)));
            }
        }
        return families;
    }

    /// <summary>Atributos da família no catálogo do ML, ou nulo se algum não casa com segurança.</summary>
    private async Task<JsonArray?> MapAsync(string token, Family family, CancellationToken ct)
    {
        if (await MatchAsync(token, "BRAND", [], family.Brand, ct) is not { } brand) return null;
        if (await MatchAsync(token, "MODEL", [("BRAND", brand)], family.Model, ct) is not { } model) return null;
        var year = family.Year.ToString(CultureInfo.InvariantCulture);
        var attributes = new JsonArray(
            new JsonObject { ["id"] = "BRAND", ["value_id"] = brand },
            new JsonObject { ["id"] = "MODEL", ["value_id"] = model },
            new JsonObject { ["id"] = "VEHICLE_YEAR", ["value_name"] = year });
        if (family.Engine is null) return attributes;

        // Motor: o ML escreve do jeito dele ("1.6 8V", "1.6 Total Flex"...). Vale o de maior sobreposição de palavras com a
        // nossa motorização, desde que a cilindrada bata e não haja empate — na dúvida, fica de fora.
        var engines = await ValuesAsync(token, "ENGINE", [("BRAND", brand), ("MODEL", model)], ct);
        if (VehicleNames.BestEngine(family.Engine, engines, e => e.Name) is not { } engine) return null;
        attributes.Add(new JsonObject { ["id"] = "ENGINE", ["value_id"] = engine.Id });
        return attributes;
    }

    private async Task<string?> MatchAsync(string token, string attribute, IReadOnlyList<(string, string)> known, string name, CancellationToken ct)
    {
        return (await ValuesAsync(token, attribute, known, ct)).FirstOrDefault(v => VehicleNames.SameName(v.Name, name))?.Id;
    }

    /// <summary>Valores do catálogo de veículos do ML (iguais para todas as lojas), em cache.</summary>
    private async Task<IReadOnlyList<AttributeValueOption>> ValuesAsync(string token, string attribute, IReadOnlyList<(string Id, string ValueId)> known, CancellationToken ct)
    {
        var key = $"ml:top:{attribute}:{string.Join(',', known.Select(k => $"{k.Id}={k.ValueId}"))}";
        if (cache.TryGetValue(key, out IReadOnlyList<AttributeValueOption>? cached) && cached is not null) return cached;
        var values = await api.TopValuesAsync(token, attribute, known, ct);
        cache.Set(key, values, CacheFor);
        return values;
    }
}
