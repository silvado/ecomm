using Ecommerce.Domain.Common;

namespace Ecommerce.Domain.Privacy;

/// <summary>
/// Registro de acesso a dados pessoais de compradores pelo painel (RNF04): quem, quando, o quê e de onde. Só inclusão —
/// o papel da aplicação não altera nem apaga (migração). Sem o dado pessoal em si: aponta para o pedido ou a exportação.
/// </summary>
public sealed class PersonalDataAccess : ITenantOwned
{
    public const int ActionMaxLength = 40;
    public const int DetailsMaxLength = 300;

    private PersonalDataAccess() { }

    public PersonalDataAccess(Guid tenantId, Guid userId, string userEmail, string action, Guid? orderId, string? details, string? ip, DateTimeOffset now)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(action);
        Id = Guid.CreateVersion7();
        TenantId = tenantId;
        UserId = userId;
        UserEmail = userEmail;
        Action = action;
        OrderId = orderId;
        Details = details is { Length: > DetailsMaxLength } ? details[..DetailsMaxLength] : details;
        Ip = ip;
        At = now;
    }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid UserId { get; private set; }

    /// <summary>Guardado à parte: o registro continua legível depois que o usuário sai da loja.</summary>
    public string UserEmail { get; private set; } = string.Empty;

    public string Action { get; private set; } = string.Empty;
    public Guid? OrderId { get; private set; }
    public string? Details { get; private set; }
    public string? Ip { get; private set; }
    public DateTimeOffset At { get; private set; }
}

/// <summary>Ações registradas (texto estável: vai para o banco e para os filtros do painel).</summary>
public static class PersonalDataActions
{
    /// <summary>Abriu o detalhe de um pedido (nome, contato, documento e endereço do comprador).</summary>
    public const string OrderViewed = "pedido.aberto";
    /// <summary>Baixou o XML ou o DANFE de uma nota (trazem os dados do destinatário).</summary>
    public const string InvoiceDownloaded = "nfe.baixada";
    public const string BuyerAnonymized = "titular.anonimizado";
    public const string ExportRequested = "exportacao.gerada";
    public const string ExportDownloaded = "exportacao.baixada";
}
