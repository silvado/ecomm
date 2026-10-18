using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ecommerce.Application.Ai;
using Ecommerce.Application.Catalog;
using Ecommerce.Domain.Ai;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Common;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Catalog;

/// <summary>
/// Cadastro assistido por foto (RF30). As fotos vão para a IA reduzidas e sem metadados; a resposta só preenche uma sugestão —
/// nada é gravado aqui (CA2) e não há preço no formato pedido (RF35). Os veículos citados pela IA são procurados na tabela da
/// plataforma: só viram sugestão de compatibilidade as versões que existem; o resto volta como texto.
/// </summary>
public sealed class PartSuggestionService(IAiCompletion ai, TenantDbContext db, TimeProvider clock) : IPartSuggestions
{
    /// <summary>Versões sugeridas por resposta: peça "universal" demais é revisada à mão.</summary>
    public const int MaxSuggestedVersions = 30;

    private const int MaxVehicleHints = 15;

    internal const string Instructions = """
        Você ajuda lojas de autopeças do Brasil (principalmente peças usadas) a cadastrar peças a partir de fotos.
        Use só o que dá para ver nas fotos ou o que a loja informou. Nunca invente.
        - titulo: como num anúncio, em português: nome da peça, lado/posição e veículo principal, se souber. Até 100 caracteres.
        - descricao: objetiva, em português: o que é a peça, lado/posição, características visíveis, estado de conservação
          visível (riscos, trincas, partes faltando) e o que acompanha. Não cite preço, garantia, frete nem contatos.
        - condicao: usada, nova ou recondicionada, pelo que se vê; na dúvida, usada.
        - codigos_oem: só códigos de fabricante legíveis nas fotos ou informados pela loja, como aparecem. Nunca deduza um código.
        - veiculos: só veículos em que a peça serve com segurança (pelo código, etiqueta ou informação da loja). Marca e modelo
          como vendidos no Brasil; motorizacao se souber, senão texto vazio; anos-modelo inicial e final (0 quando não souber).
          Na dúvida, deixe a lista vazia.
        Você não define preço nem estoque: a loja revisa tudo antes de salvar.
        """;

    /// <summary>Formato da resposta (structured outputs). Anos 0 = não informado.</summary>
    internal static readonly JsonObject OutputSchema = new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["titulo"] = new JsonObject { ["type"] = "string" },
            ["descricao"] = new JsonObject { ["type"] = "string" },
            ["condicao"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("usada", "nova", "recondicionada") },
            ["codigos_oem"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
            ["veiculos"] = new JsonObject
            {
                ["type"] = "array",
                ["items"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["marca"] = new JsonObject { ["type"] = "string" },
                        ["modelo"] = new JsonObject { ["type"] = "string" },
                        ["motorizacao"] = new JsonObject { ["type"] = "string" },
                        ["ano_inicial"] = new JsonObject { ["type"] = "integer" },
                        ["ano_final"] = new JsonObject { ["type"] = "integer" },
                    },
                    ["required"] = new JsonArray("marca", "modelo", "motorizacao", "ano_inicial", "ano_final"),
                    ["additionalProperties"] = false,
                },
            },
        },
        ["required"] = new JsonArray("titulo", "descricao", "condicao", "codigos_oem", "veiculos"),
        ["additionalProperties"] = false,
    };

    /// <summary>
    /// Resposta da IA simulada (dev): um veículo que existe na amostra de veículos (modelo com a geração junto, como a IA
    /// costuma escrever) e um que não existe.
    /// </summary>
    internal static readonly JsonObject DevSample = new()
    {
        ["titulo"] = "Farol dianteiro esquerdo Gol G5",
        ["descricao"] = "Farol dianteiro do lado esquerdo (motorista), com máscara negra. Lente com riscos leves de uso; "
            + "carcaça e presilhas inteiras. Acompanha o motor de regulagem. Peça usada, retirada de veículo em funcionamento.",
        ["condicao"] = "usada",
        ["codigos_oem"] = new JsonArray("5U0 941 015"),
        ["veiculos"] = new JsonArray(
            new JsonObject { ["marca"] = "Volkswagen", ["modelo"] = "Gol G5", ["motorizacao"] = "", ["ano_inicial"] = 2009, ["ano_final"] = 2012 },
            new JsonObject { ["marca"] = "Volkswagen", ["modelo"] = "Voyage", ["motorizacao"] = "1.6", ["ano_inicial"] = 2009, ["ano_final"] = 2012 },
            new JsonObject { ["marca"] = "Fiat", ["modelo"] = "Tempra", ["motorizacao"] = "2.0", ["ano_inicial"] = 1995, ["ano_final"] = 1998 }),
    };

    private sealed record VehicleHint(string Brand, string Model, string Engine, int YearFrom, int YearTo)
    {
        public string Label =>
            string.Join(' ', new[] { Brand, Model, Engine }.Where(s => s.Length > 0))
            + (YearFrom > 0 || YearTo > 0 ? $" {(YearFrom > 0 ? YearFrom : "?")}–{(YearTo > 0 ? YearTo : "?")}" : string.Empty);
    }

    public async Task<PartSuggestionResult> SuggestAsync(IReadOnlyList<SuggestionPhoto> photos, string? notes, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(photos);
        if (photos.Count == 0) return Invalid("Envie ao menos uma foto da peça.");
        if (photos.Count > IPartSuggestions.MaxPhotos) return Invalid($"Envie no máximo {IPartSuggestions.MaxPhotos} fotos.");
        var text = notes?.Trim() ?? string.Empty;
        if (text.Length > IPartSuggestions.MaxNotesLength) return Invalid($"As informações podem ter no máximo {IPartSuggestions.MaxNotesLength} caracteres.");

        var images = new List<AiImage>();
        foreach (var (photo, index) in photos.Select((p, i) => (p, i + 1)))
        {
            if (photo.Content.Length == 0) return Invalid($"Foto {index}: arquivo vazio.");
            if (photo.Content.Length > PartPhoto.MaxUploadBytes) return Invalid($"Foto {index}: a foto pode ter no máximo 10 MB.");
            if (ImageSignature.DetectContentType(photo.Content.AsSpan(0, Math.Min(photo.Content.Length, ImageSignature.HeaderLength))) is null)
                return Invalid($"Foto {index}: formato não aceito. Envie JPG, PNG ou WebP.");
            var (jpeg, error) = await PhotoProcessor.ForAiAsync(photo.Content, ct);
            if (jpeg is null) return Invalid($"Foto {index}: {error}");
            images.Add(new AiImage("image/jpeg", jpeg));
        }

        var prompt = $"Fotos da peça: {images.Count}.\nInformações da loja: {(text.Length > 0 ? text : "nenhuma")}";
        var summary = $"{images.Count} foto(s); informações: {(text.Length > 0 ? text : "nenhuma")}";
        AiResult result;
        try
        {
            result = await ai.CompleteAsync(new AiRequest(AiFeature.PartFromPhotos, Instructions, prompt, images, OutputSchema, summary), ct);
        }
        catch (AiQuotaExceededException e)
        {
            return new PartSuggestionResult(SuggestionOutcome.QuotaExceeded, Message: e.Message);
        }
        catch (AiUnavailableException e)
        {
            return new PartSuggestionResult(SuggestionOutcome.Unavailable, Message: e.Message, Transient: e.Transient);
        }
        catch (AiInvalidOutputException)
        {
            return new PartSuggestionResult(SuggestionOutcome.Unusable,
                Message: "A IA não conseguiu sugerir um cadastro com estas fotos. Tente fotos mais nítidas (etiqueta e código à mostra) ou preencha à mão.");
        }

        var output = result.Output;
        var (compatibilities, unmatched) = await MatchVehiclesAsync(Hints(output), ct);
        return new PartSuggestionResult(SuggestionOutcome.Done, new PartSuggestion(
            Cut(String(output, "titulo"), Part.TitleMaxLength),
            Cut(String(output, "descricao"), Part.DescriptionMaxLength),
            String(output, "condicao") switch { "nova" => PartCondition.New, "recondicionada" => PartCondition.Refurbished, _ => PartCondition.Used },
            OemCodes(output),
            compatibilities,
            unmatched));
    }

    /// <summary>
    /// Marca por nome; modelo pelo nome inteiro ou como começo do texto da IA ("Gol G5" → Gol, sobra "G5"); a sobra e a
    /// motorização precisam aparecer na motorização da versão; anos cortam as versões e restringem dentro delas.
    /// </summary>
    private async Task<(List<SuggestedCompatibility>, List<string>)> MatchVehiclesAsync(List<VehicleHint> hints, CancellationToken ct)
    {
        var found = new List<SuggestedCompatibility>();
        var unmatched = new List<string>();
        if (hints.Count == 0) return (found, unmatched);

        var brands = await db.VehicleBrands.AsNoTracking().ToListAsync(ct);
        var currentYear = clock.GetUtcNow().Year;
        foreach (var hint in hints)
        {
            var brand = brands.FirstOrDefault(b => Tokens(b.Name).SequenceEqual(Tokens(hint.Brand)));
            var hintModel = Tokens(hint.Model);
            if (brand is null)
            {
                unmatched.Add(hint.Label);
                continue;
            }
            var (model, modelTokens) = (await db.VehicleModels.AsNoTracking().Where(m => m.BrandId == brand.Id).ToListAsync(ct))
                .Select(m => (Model: (Domain.Vehicles.VehicleModel?)m, Tokens: Tokens(m.Name)))
                .Where(m => m.Tokens.Count > 0 && m.Tokens.Count <= hintModel.Count && m.Tokens.SequenceEqual(hintModel.Take(m.Tokens.Count)))
                .OrderByDescending(m => m.Tokens.Count)
                .FirstOrDefault();
            if (model is null)
            {
                unmatched.Add(hint.Label);
                continue;
            }

            var engine = hintModel.Skip(modelTokens.Count).Concat(Tokens(hint.Engine)).ToList();
            int? from = hint.YearFrom > 0 ? hint.YearFrom : null;
            int? to = hint.YearTo > 0 ? hint.YearTo : null;
            if (from > to) (from, to) = (to, from);
            var versions = (await db.VehicleVersions.AsNoTracking().Where(v => v.ModelId == model.Id && !v.Discontinued).ToListAsync(ct))
                .Where(v => engine.All(Tokens(v.Engine).Contains))
                .Where(v => (to is null || v.YearFrom <= to) && (from is null || v.YearTo is null || v.YearTo >= from))
                .OrderBy(v => v.YearFrom).ThenBy(v => v.Engine)
                .ToList();
            if (versions.Count == 0)
            {
                unmatched.Add(hint.Label);
                continue;
            }

            foreach (var version in versions)
            {
                if (found.Count >= MaxSuggestedVersions || found.Any(f => f.VehicleVersionId == version.Id)) continue;
                var low = Math.Max(from ?? version.YearFrom, version.YearFrom);
                var high = (to, version.YearTo) switch
                {
                    (null, var end) => end,
                    ({ } t, null) => t,
                    ({ } t, { } end) => Math.Min(t, end),
                };
                (int? yearFrom, int? yearTo) narrow = low == version.YearFrom && high == version.YearTo
                    ? (null, null)
                    : (low, high ?? Math.Max(low, currentYear));
                found.Add(new SuggestedCompatibility(version.Id, brand.Name, model.Name, version.Engine, version.YearFrom, version.YearTo,
                    narrow.yearFrom, narrow.yearTo));
            }
        }
        return (found, unmatched);
    }

    private static List<VehicleHint> Hints(JsonElement output)
    {
        if (!output.TryGetProperty("veiculos", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        return list.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.Object)
            .Select(v => new VehicleHint(Cut(String(v, "marca"), 60), Cut(String(v, "modelo"), 80), Cut(String(v, "motorizacao"), 80), Year(v, "ano_inicial"), Year(v, "ano_final")))
            .Where(v => v.Brand.Length > 0 && v.Model.Length > 0)
            .Take(MaxVehicleHints)
            .ToList();
    }

    private static List<string> OemCodes(JsonElement output)
    {
        if (!output.TryGetProperty("codigos_oem", out var list) || list.ValueKind != JsonValueKind.Array) return [];
        var codes = new List<string>();
        foreach (var item in list.EnumerateArray().Where(i => i.ValueKind == JsonValueKind.String))
        {
            var raw = item.GetString()!.Trim();
            string normalized;
            try { normalized = PartOemCode.Normalize(raw); }
            catch (ArgumentException) { continue; } // longo demais: não é código
            if (normalized.Length > 0 && codes.All(c => PartOemCode.Normalize(c) != normalized)) codes.Add(raw);
        }
        return codes.Take(Part.MaxOemCodes).ToList();
    }

    /// <summary>Minúsculas, sem acento, só letras, dígitos e ponto ("1.0 8V Flex (G5)" → 1.0, 8v, flex, g5).</summary>
    internal static List<string> Tokens(string? text) => VehicleNames.Tokens(text);

    private static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()!.Trim() : string.Empty;

    private static int Year(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var year) && year >= Domain.Vehicles.VehicleVersion.MinYear && year <= 2100 ? year : 0;

    private static string Cut(string text, int max) => text.Length <= max ? text : text[..max].TrimEnd();

    private static PartSuggestionResult Invalid(string message) => new(SuggestionOutcome.Invalid, Message: message);
}
