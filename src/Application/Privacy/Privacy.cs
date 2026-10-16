namespace Ecommerce.Application.Privacy;

/// <summary>Quem está usando o painel nesta requisição (preenchido pela API; lido pelo registro de acesso).</summary>
public sealed class PanelActor
{
    public Guid UserId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string? Ip { get; set; }
}

public sealed record PersonalDataAccessItem(DateTimeOffset At, string UserEmail, string Action, Guid? OrderId, long? OrderNumber, string? Details, string? Ip);

public sealed record PersonalDataAccessPage(IReadOnlyList<PersonalDataAccessItem> Items, int Page, int PageSize, bool HasMore);

public sealed record PersonalDataAccessFilter(string? Action = null, Guid? OrderId = null, int Page = 1, int PageSize = 50);

/// <summary>
/// Registro de acesso a dados pessoais (RNF04): abrir pedido, baixar nota, anonimizar, exportar. <paramref name="details"/>
/// nunca leva o dado pessoal em si.
/// </summary>
public interface IPersonalDataAccessLog
{
    Task RecordAsync(string action, Guid? orderId = null, string? details = null, CancellationToken ct = default);

    Task<PersonalDataAccessPage> ListAsync(PersonalDataAccessFilter filter, CancellationToken ct = default);
}

/// <summary>Titular procurado pelo e-mail ou pelo CPF/CNPJ que informou nos pedidos.</summary>
public sealed record DataSubject(string? Email, string? Document)
{
    public override string ToString() => "DataSubject(***)";
}

/// <param name="Orders">Pedidos encontrados com o e-mail ou documento.</param>
/// <param name="InProgress">Números dos pedidos em andamento: enquanto houver algum, nada é anonimizado.</param>
/// <param name="WithInvoice">Pedidos com nota fiscal: nome e CPF/CNPJ ficam (lei fiscal), o resto sai.</param>
/// <param name="AlreadyAnonymized">Já anonimizados antes (nada muda neles).</param>
public sealed record AnonymizationPreview(int Orders, IReadOnlyList<long> InProgress, int WithInvoice, int AlreadyAnonymized);

public sealed record AnonymizationResult(AnonymizationPreview? Preview, string? Error);

/// <summary>
/// Anonimização do comprador a pedido dele (RNF04, LGPD art. 18): apaga e-mail, telefone e endereço de entrega dos
/// pedidos concluídos e o acesso à área "Meus pedidos"; sem nota fiscal, também nome e CPF/CNPJ.
/// </summary>
public interface IBuyerAnonymization
{
    Task<(AnonymizationPreview? Preview, string? Error)> PreviewAsync(DataSubject subject, CancellationToken ct = default);

    Task<AnonymizationResult> AnonymizeAsync(DataSubject subject, CancellationToken ct = default);
}
