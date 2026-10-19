using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Catalog;

/// <summary>
/// Último número de código gerado da loja (<see cref="Part.GeneratedCode"/>). Incrementado por um único UPSERT na transação
/// do cadastro: a linha travada serializa cadastros simultâneos da mesma loja, e um cadastro desfeito devolve o número.
/// </summary>
public sealed class PartCodeCounter : ITenantOwned
{
    private PartCodeCounter() { }

    public Guid TenantId { get; private set; }
    public long LastNumber { get; private set; }
}
