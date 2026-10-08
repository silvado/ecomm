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
