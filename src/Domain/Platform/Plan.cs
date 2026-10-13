namespace Ecommerce.Domain.Platform;

/// <summary>Planos da assinatura (RF37). Os limites ficam em tabela e mudam sem deploy.</summary>
public sealed class Plan
{
    public const string Essential = "essencial";
    public const string Professional = "profissional";
    public const string Complete = "completo";

    private readonly List<PlanLimit> _limits = [];

    private Plan() { }

    public Plan(Guid id, string code, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Id = id;
        Code = code;
        Name = name;
    }

    public Guid Id { get; private set; }
    public string Code { get; private set; } = string.Empty;
    public string Name { get; private set; } = string.Empty;
    public IReadOnlyCollection<PlanLimit> Limits => _limits;

    /// <summary>Nulo = sem limite.</summary>
    public int? Limit(string key) => _limits.SingleOrDefault(l => l.Key == key)?.Value;
}

public sealed class PlanLimit
{
    /// <summary>Usuários com acesso ao painel da loja (RF07 CA2).</summary>
    public const string Users = "users";

    /// <summary>Peças à venda ao mesmo tempo (RF37). Sem linha = sem limite.</summary>
    public const string ActiveParts = "active_parts";

    /// <summary>Cadastros de peça com IA por mês (RF36/RF37; o bloqueio entra com a IA).</summary>
    public const string AiListingsPerMonth = "ai_listings_month";

    private PlanLimit() { }

    public PlanLimit(string planCode, string key, int value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentOutOfRangeException.ThrowIfNegative(value);
        PlanCode = planCode;
        Key = key;
        Value = value;
    }

    public string PlanCode { get; private set; } = string.Empty;
    public string Key { get; private set; } = string.Empty;
    public int Value { get; private set; }
}

/// <summary>Recurso habilitado num plano (RF37 — feature flag como dado). Sem linha = o plano não inclui.</summary>
public sealed class PlanFeature
{
    /// <summary>Canal de venda: "channel:" + nome do canal (ex.: channel:mercado-livre).</summary>
    public const string ChannelPrefix = "channel:";
    public const string MercadoLivre = ChannelPrefix + "mercado-livre";
    public const string Meta = ChannelPrefix + "meta";
    public const string Olx = ChannelPrefix + "olx";
    public const string CustomDomain = "custom_domain";
    public const string AiPartFromPhotos = "ai:part_from_photos";
    public const string AiBuyerAnswers = "ai:buyer_answers";
    public const string AiPriceSuggestion = "ai:price_suggestion";
    public const string AiDailySummary = "ai:daily_summary";

    private PlanFeature() { }

    public PlanFeature(string planCode, string feature)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(planCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(feature);
        PlanCode = planCode;
        Feature = feature;
    }

    public string PlanCode { get; private set; } = string.Empty;
    public string Feature { get; private set; } = string.Empty;

    public static string ForChannel(string channel) => ChannelPrefix + channel;
}
