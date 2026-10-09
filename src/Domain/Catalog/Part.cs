using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Catalog;

public enum PartCondition
{
    New,
    Used,
    Refurbished,
}

public enum PartStatus
{
    /// <summary>Em preparação: não aparece na loja nem nos canais.</summary>
    Draft,
    Active,
    /// <summary>Fora de venda. Não há exclusão: a peça tem histórico de estoque, pedidos e anúncios.</summary>
    Inactive,
}

/// <summary>
/// Dados editáveis da peça (RF08 CA1). Dimensões e peso são da <b>embalagem</b>, para frete; nulos = não informados.
/// </summary>
public sealed record PartDetails(
    string Title,
    string? Description,
    PartCondition Condition,
    decimal Price,
    int? LengthCm,
    int? WidthCm,
    int? HeightCm,
    int? WeightG,
    IReadOnlyCollection<string>? OemCodes);

/// <summary>
/// Peça do catálogo (RF08). A quantidade não mora aqui: é o <see cref="Inventory.Stock"/>, alterado só por
/// UPDATE condicional no banco (RNF02).
/// </summary>
public sealed class Part : ITenantOwned
{
    public const int InternalCodeMaxLength = 60;
    public const int TitleMaxLength = 120;
    public const int DescriptionMaxLength = 5000;
    public const int MaxOemCodes = 30;
    public const int MaxDimensionCm = 1000;
    public const int MaxWeightG = 1_000_000;
    public const decimal MaxPrice = 9_999_999_999.99m; // numeric(12,2)

    /// <summary>RF08 CA1: de 1 a 20 fotos para estar à venda.</summary>
    public const int MaxPhotos = 20;

    private readonly List<PartOemCode> _oemCodes = [];
    private readonly List<PartPhoto> _photos = [];

    private Part() { }

    public static Part Create(Guid tenantId, string internalCode, PartDetails details, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty) throw new ArgumentException("Tenant obrigatório.", nameof(tenantId));
        var part = new Part
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            InternalCode = NormalizeInternalCode(internalCode),
            Status = PartStatus.Draft,
            CreatedAt = now,
        };
        part.Update(details, now);
        return part;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }

    /// <summary>Código da loja, único por tenant (CA2). Maiúsculo, sem espaços nas pontas.</summary>
    public string InternalCode { get; private set; } = string.Empty;

    public string Title { get; private set; } = string.Empty;
    public string Description { get; private set; } = string.Empty;
    public PartCondition Condition { get; private set; }

    /// <summary>Em reais (BRL), duas casas.</summary>
    public decimal Price { get; private set; }

    public int? LengthCm { get; private set; }
    public int? WidthCm { get; private set; }
    public int? HeightCm { get; private set; }
    public int? WeightG { get; private set; }
    public PartStatus Status { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public IReadOnlyCollection<PartOemCode> OemCodes => _oemCodes;

    /// <summary>Em ordem de exibição (a primeira é a capa).</summary>
    public IReadOnlyList<PartPhoto> Photos => _photos.OrderBy(p => p.Position).ToList();

    /// <summary>Registra uma foto já gravada no storage, no fim da lista.</summary>
    public PartPhoto AddPhoto(Guid photoId, string originalContentType, DateTimeOffset now)
    {
        if (_photos.Count >= MaxPhotos) throw new InvalidOperationException($"A peça já tem {MaxPhotos} fotos.");
        var photo = new PartPhoto(TenantId, Id, photoId, _photos.Count, originalContentType, now);
        _photos.Add(photo);
        UpdatedAt = now;
        return photo;
    }

    /// <summary>Peça à venda precisa de ao menos uma foto (RF08 CA1): a última não sai enquanto ela estiver ativa.</summary>
    public PartPhoto RemovePhoto(Guid photoId, DateTimeOffset now)
    {
        var photo = _photos.SingleOrDefault(p => p.Id == photoId) ?? throw new KeyNotFoundException("Foto não encontrada.");
        if (Status == PartStatus.Active && _photos.Count == 1)
            throw new InvalidOperationException("Peça ativa precisa de ao menos uma foto. Inative a peça ou envie outra foto antes.");
        _photos.Remove(photo);
        Renumber(_photos.OrderBy(p => p.Position));
        UpdatedAt = now;
        return photo;
    }

    /// <summary>Nova ordem: precisa listar exatamente as fotos da peça, cada uma uma vez.</summary>
    public void ReorderPhotos(IReadOnlyList<Guid> photoIds, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(photoIds);
        if (photoIds.Count != _photos.Count || photoIds.Distinct().Count() != photoIds.Count || photoIds.Any(id => _photos.All(p => p.Id != id)))
            throw new ArgumentException("A nova ordem precisa conter cada foto da peça exatamente uma vez.", nameof(photoIds));
        Renumber(photoIds.Select(id => _photos.Single(p => p.Id == id)));
        UpdatedAt = now;
    }

    private static void Renumber(IEnumerable<PartPhoto> ordered)
    {
        var position = 0;
        foreach (var photo in ordered.ToList()) photo.Position = position++;
    }

    /// <summary>RF08 CA4: sem as quatro medidas da embalagem, não publica em canal que calcula frete.</summary>
    public bool HasShippingDimensions => LengthCm is not null && WidthCm is not null && HeightCm is not null && WeightG is not null;

    public void Update(PartDetails details, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(details);
        var title = details.Title?.Trim() ?? string.Empty;
        if (title.Length is 0 or > TitleMaxLength) throw new ArgumentException($"Título obrigatório, até {TitleMaxLength} caracteres.", nameof(details));
        var description = (details.Description ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (description.Length > DescriptionMaxLength) throw new ArgumentException($"Descrição acima de {DescriptionMaxLength} caracteres.", nameof(details));
        if (!Enum.IsDefined(details.Condition)) throw new ArgumentException("Estado da peça inválido.", nameof(details));
        if (details.Price <= 0 || details.Price > MaxPrice || decimal.Round(details.Price, 2) != details.Price)
            throw new ArgumentException("Preço precisa ser maior que zero, com até duas casas decimais.", nameof(details));
        Dimension(details.LengthCm, MaxDimensionCm, "Comprimento");
        Dimension(details.WidthCm, MaxDimensionCm, "Largura");
        Dimension(details.HeightCm, MaxDimensionCm, "Altura");
        Dimension(details.WeightG, MaxWeightG, "Peso");
        var oem = (details.OemCodes ?? []).Select(PartOemCode.Normalize).Where(c => c.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (oem.Count > MaxOemCodes) throw new ArgumentException($"No máximo {MaxOemCodes} códigos OEM.", nameof(details));

        Title = title;
        Description = description;
        Condition = details.Condition;
        Price = details.Price;
        (LengthCm, WidthCm, HeightCm, WeightG) = (details.LengthCm, details.WidthCm, details.HeightCm, details.WeightG);
        _oemCodes.RemoveAll(c => !oem.Contains(c.Code));
        foreach (var code in oem.Where(code => _oemCodes.All(c => c.Code != code)))
            _oemCodes.Add(new PartOemCode(TenantId, Id, code));
        UpdatedAt = now;
    }

    /// <summary>Coloca à venda: exige ao menos uma foto (RF08 CA1). Carregar a peça com as fotos antes.</summary>
    public void Activate(DateTimeOffset now)
    {
        if (_photos.Count == 0) throw new InvalidOperationException("Envie ao menos uma foto antes de ativar a peça.");
        Status = PartStatus.Active;
        UpdatedAt = now;
    }

    public void Deactivate(DateTimeOffset now)
    {
        Status = PartStatus.Inactive;
        UpdatedAt = now;
    }

    public void Rename(string title) => Title = string.IsNullOrWhiteSpace(title) ? throw new ArgumentException("Título obrigatório.", nameof(title)) : title.Trim();

    public static string NormalizeInternalCode(string? code)
    {
        var value = code?.Trim().ToUpperInvariant() ?? string.Empty;
        if (value.Length is 0 or > InternalCodeMaxLength || value.Any(char.IsControl))
            throw new ArgumentException($"Código interno obrigatório, até {InternalCodeMaxLength} caracteres.", nameof(code));
        return value;
    }

    private static void Dimension(int? value, int max, string name)
    {
        if (value is not null && (value <= 0 || value > max))
            throw new ArgumentException($"{name} precisa estar entre 1 e {max}.", nameof(value));
    }
}

/// <summary>
/// Código do fabricante (OEM) da peça. Guardado normalizado — maiúsculo, só letras e dígitos — para que
/// "1J0 615 301-M" e "1j0615301m" sejam o mesmo código na busca.
/// </summary>
public sealed class PartOemCode : ITenantOwned
{
    public const int MaxLength = 30;

    private PartOemCode() { }

    internal PartOemCode(Guid tenantId, Guid partId, string code)
    {
        TenantId = tenantId;
        PartId = partId;
        Code = code;
    }

    public Guid TenantId { get; private set; }
    public Guid PartId { get; private set; }
    public string Code { get; private set; } = string.Empty;

    public static string Normalize(string? code)
    {
        var value = new string((code ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToUpperInvariant();
        if (value.Length > MaxLength) throw new ArgumentException($"Código OEM acima de {MaxLength} caracteres.", nameof(code));
        return value;
    }
}
