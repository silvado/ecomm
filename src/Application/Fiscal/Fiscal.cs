using Ecommerce.Domain.Fiscal;
using Ecommerce.Domain.Orders;

namespace Ecommerce.Application.Fiscal;

// NF-e da loja (RF19, Q37). O provedor fiscal fica atrás de IFiscalProvider: o primeiro adaptador real é o da Focus NFe
// (parte B); dev e testes usam o falso. A forma segue a dos provedores de NF-e por API: envio assíncrono identificado por
// uma referência nossa, consulta da situação, cancelamento com justificativa e download do XML e do DANFE.

public enum FiscalPresence
{
    /// <summary>Venda pela internet (loja virtual).</summary>
    Internet,
    /// <summary>Venda presencial (balcão).</summary>
    Presential,
}

public sealed record FiscalIssuer(string Cnpj, string State, TaxRegime Regime);

public sealed record FiscalInvoiceItem(string Code, string Description, string Ncm, string Cfop, string TaxCode, int Origin, int Quantity, decimal UnitPrice);

/// <summary>Nota de venda pronta para o provedor (dados já conferidos e com os padrões da loja aplicados).</summary>
public sealed record FiscalInvoice(string Reference, FiscalEnvironment Environment, FiscalIssuer Issuer, FiscalRecipient Recipient,
    FiscalPresence Presence, bool FinalConsumer, IReadOnlyList<FiscalInvoiceItem> Items, decimal ShippingTotal, decimal Total, DateTimeOffset IssuedAt);

public enum FiscalProviderStatus
{
    Processing,
    Authorized,
    Rejected,
    Canceled,
}

public sealed record FiscalProviderResult(FiscalProviderStatus Status, string? AccessKey = null, int? Number = null, int? Series = null, string? Message = null);

public enum FiscalFile
{
    Xml,
    Danfe,
}

/// <summary>Falha de comunicação com o provedor (fora do ar, tempo esgotado): a fila tenta de novo (RF28).</summary>
public sealed class FiscalProviderException(string message, Exception? inner = null) : Exception(message, inner);

public interface IFiscalProvider
{
    /// <summary>Nome para o log de integração e para o documento (ex.: "focus-nfe").</summary>
    string Name { get; }

    Task<FiscalProviderResult> EmitAsync(FiscalInvoice invoice, CancellationToken ct = default);

    Task<FiscalProviderResult> QueryAsync(string reference, CancellationToken ct = default);

    Task<FiscalProviderResult> CancelAsync(string reference, string justification, CancellationToken ct = default);

    /// <summary>Nulo se o provedor ainda não tem o arquivo.</summary>
    Task<byte[]?> DownloadAsync(string reference, FiscalFile file, CancellationToken ct = default);
}

/// <summary>Provedor fiscal da loja do escopo, se ela emite pela plataforma e está conectada.</summary>
public interface IFiscalProviderResolver
{
    Task<IFiscalProvider?> ResolveAsync(CancellationToken ct = default);
}

// Comandos (handlers na Infrastructure). Quem chama usa IMessageBus.InvokeForTenantAsync.

/// <summary>Pede a emissão da NF-e de venda. <paramref name="Recipient"/> completa o que falta no pedido (endereço, CPF/CNPJ).</summary>
public sealed record EmitInvoice(Guid OrderId, Guid UserId, FiscalRecipient? Recipient);

/// <summary>Envia ao provedor (fila, com novas tentativas).</summary>
public sealed record SendInvoice(Guid DocumentId);

/// <summary>Consulta a situação até a SEFAZ responder (agendada).</summary>
public sealed record CheckInvoice(Guid DocumentId, int Attempt);

public sealed record CancelInvoice(Guid DocumentId, Guid UserId, string? Justification);

/// <summary>NF-e emitida fora: chave e, opcionalmente, o XML (conferido contra a chave e o CNPJ da loja).</summary>
public sealed record RegisterExternalInvoice(Guid OrderId, Guid UserId, string? AccessKey, byte[]? Xml);

public enum FiscalCommandOutcome
{
    Done,
    NotFound,
    /// <summary>Não vale agora (pedido não pago, já há nota ativa, fora do prazo, modo errado).</summary>
    NotAllowed,
    Invalid,
    /// <summary>O provedor recusou na hora (ex.: cancelamento negado pela SEFAZ).</summary>
    Refused,
}

public sealed record FiscalCommandResult(FiscalCommandOutcome Outcome, string? Message = null, Guid? DocumentId = null)
{
    public static readonly FiscalCommandResult Ok = new(FiscalCommandOutcome.Done);
}

public sealed record FiscalDocumentView(Guid Id, FiscalDocumentSource Source, FiscalDocumentStatus Status, FiscalEnvironment? Environment,
    string? AccessKey, int? Number, int? Series, string? Message, DateTimeOffset RequestedAt, DateTimeOffset? AuthorizedAt,
    DateTimeOffset? CanceledAt, string? CancelJustification, bool HasXml, bool HasDanfe, bool CanCancel, DateTimeOffset? CancelDeadline);

/// <summary>
/// O que o painel precisa para emitir: modo da loja, destinatário sugerido (do pedido) e o que impede a emissão
/// (configuração incompleta, peça sem NCM, pedido não pago...).
/// </summary>
public sealed record InvoiceDraft(FiscalMode Mode, FiscalRecipient? Recipient, IReadOnlyList<string> Problems, IReadOnlyList<FiscalDocumentView> Documents);

public sealed record FiscalSettingsView(FiscalSettings Settings, string IssuerCnpj, bool ProviderConnected);

public interface IFiscalService
{
    Task<FiscalSettingsView> GetSettingsAsync(CancellationToken ct = default);

    /// <exception cref="ArgumentException">Dados inválidos (mensagem ao usuário).</exception>
    Task<FiscalSettingsView> SaveSettingsAsync(FiscalSettings settings, CancellationToken ct = default);

    Task<InvoiceDraft?> DraftAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>Dados fiscais da peça (vazios = padrão da loja). Falso se a peça não existe.</summary>
    /// <exception cref="ArgumentException">Código inválido para o regime da loja.</exception>
    Task<bool> SetPartFiscalAsync(Guid partId, PartFiscalData data, CancellationToken ct = default);

    /// <summary>XML ou DANFE guardado da nota; nulo se não existe.</summary>
    Task<(Application.Storage.StoredFile File, string FileName)?> FileAsync(Guid documentId, FiscalFile file, CancellationToken ct = default);
}

/// <summary>Situações do pedido em que a NF-e de venda pode ser emitida.</summary>
public static class InvoiceRules
{
    public static bool OrderAccepts(OrderStatus status) =>
        status is OrderStatus.Paid or OrderStatus.Shipped or OrderStatus.Delivered or OrderStatus.PickedUp;
}
