using Ecommerce.Domain.Common;
using Ecommerce.Domain.Orders;
using Ecommerce.Domain.Platform;

namespace Ecommerce.Domain.Fiscal;

public enum FiscalDocumentSource
{
    /// <summary>Emitida pela plataforma, no provedor fiscal da loja.</summary>
    Provider,
    /// <summary>Emitida fora (ERP, emissor próprio): a loja só registrou a chave ou o XML.</summary>
    External,
}

public enum FiscalDocumentStatus
{
    /// <summary>Enviada ao provedor, aguardando a SEFAZ.</summary>
    Processing,
    Authorized,
    /// <summary>Recusada pela SEFAZ ou pelo provedor: corrigir e reenviar (nova tentativa).</summary>
    Rejected,
    Canceled,
}

/// <summary>Destinatário da NF-e modelo 55: nome, CPF ou CNPJ e endereço são obrigatórios (Q37 — NF-e 55 sempre).</summary>
public sealed record FiscalRecipient(string Name, string Document, string? Email, DeliveryAddress? Address)
{
    public const int NameMaxLength = 60;

    public static FiscalRecipient Normalize(FiscalRecipient recipient)
    {
        ArgumentNullException.ThrowIfNull(recipient);
        var name = string.Join(' ', (recipient.Name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length is < 2 or > NameMaxLength) throw new ArgumentException($"Informe o nome do destinatário (até {NameMaxLength} caracteres).", nameof(recipient));
        string document;
        if (Cpf.TryParse(recipient.Document, out var cpf)) document = cpf!.Value;
        else if (Cnpj.TryParse(recipient.Document, out var cnpj)) document = cnpj!.Value;
        else throw new ArgumentException("Informe o CPF ou CNPJ do destinatário.", nameof(recipient));
        var email = string.IsNullOrWhiteSpace(recipient.Email) ? null : recipient.Email.Trim().ToLowerInvariant();
        if (recipient.Address is null) throw new ArgumentException("Informe o endereço do destinatário.", nameof(recipient));
        return new FiscalRecipient(name, document, email, CustomerOrder.NormalizeAddress(recipient.Address));
    }
}

/// <summary>
/// NF-e de venda do pedido (RF19). Cada tentativa de emissão é um documento (a rejeitada fica no histórico); só pode haver
/// uma em processamento ou autorizada por pedido. A referência é o identificador da nota no provedor.
/// </summary>
public sealed class FiscalDocument : ITenantOwned
{
    public const int ReferenceMaxLength = 50;
    public const int MessageMaxLength = 1000;
    public const int JustificationMinLength = 15;
    public const int JustificationMaxLength = 255;

    /// <summary>Prazo para cancelar a NF-e autorizada (padrão nacional: 24 horas; fora dele, NF-e de devolução).</summary>
    public static readonly TimeSpan CancelWindow = TimeSpan.FromHours(24);

    private FiscalDocument() { }

    public Guid Id { get; private set; }
    public Guid TenantId { get; private set; }
    public Guid OrderId { get; private set; }
    public FiscalDocumentSource Source { get; private set; }
    public FiscalDocumentStatus Status { get; private set; }
    public string? Reference { get; private set; }
    public string? Provider { get; private set; }
    public FiscalEnvironment? Environment { get; private set; }
    public FiscalRecipient? Recipient { get; private set; }
    public string? AccessKey { get; private set; }
    public int? Number { get; private set; }
    public int? Series { get; private set; }
    public string? Message { get; private set; }
    public string? XmlStorageKey { get; private set; }
    public string? DanfeStorageKey { get; private set; }
    public Guid? RequestedBy { get; private set; }
    public DateTimeOffset RequestedAt { get; private set; }
    public DateTimeOffset? AuthorizedAt { get; private set; }
    public DateTimeOffset? CanceledAt { get; private set; }
    public string? CancelJustification { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Nova emissão pelo provedor, aguardando a SEFAZ.</summary>
    public static FiscalDocument Request(Guid tenantId, Guid orderId, string reference, string provider, FiscalEnvironment environment,
        FiscalRecipient recipient, Guid? userId, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || orderId == Guid.Empty) throw new ArgumentException("Loja e pedido obrigatórios.", nameof(orderId));
        if (string.IsNullOrWhiteSpace(reference) || reference.Length > ReferenceMaxLength) throw new ArgumentException("Referência inválida.", nameof(reference));
        return new FiscalDocument
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OrderId = orderId,
            Source = FiscalDocumentSource.Provider,
            Status = FiscalDocumentStatus.Processing,
            Reference = reference,
            Provider = provider,
            Environment = environment,
            Recipient = FiscalRecipient.Normalize(recipient),
            RequestedBy = userId,
            RequestedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>NF-e emitida fora da plataforma: já nasce autorizada, com a chave informada (e o XML, se anexado).</summary>
    public static FiscalDocument RegisterExternal(Guid tenantId, Guid orderId, string accessKey, string? xmlStorageKey, Guid? userId, DateTimeOffset now)
    {
        if (tenantId == Guid.Empty || orderId == Guid.Empty) throw new ArgumentException("Loja e pedido obrigatórios.", nameof(orderId));
        var key = Fiscal.AccessKey.Normalize(accessKey);
        return new FiscalDocument
        {
            Id = Guid.CreateVersion7(),
            TenantId = tenantId,
            OrderId = orderId,
            Source = FiscalDocumentSource.External,
            Status = FiscalDocumentStatus.Authorized,
            AccessKey = key,
            Number = int.Parse(key.AsSpan(25, 9), System.Globalization.CultureInfo.InvariantCulture),
            Series = int.Parse(key.AsSpan(22, 3), System.Globalization.CultureInfo.InvariantCulture),
            XmlStorageKey = xmlStorageKey,
            RequestedBy = userId,
            RequestedAt = now,
            AuthorizedAt = now,
            UpdatedAt = now,
        };
    }

    public bool IsActive => Status is FiscalDocumentStatus.Processing or FiscalDocumentStatus.Authorized;

    public void Authorize(string accessKey, int? number, int? series, DateTimeOffset now)
    {
        RequireProcessing();
        AccessKey = Fiscal.AccessKey.Normalize(accessKey);
        Number = number;
        Series = series;
        Message = null;
        Status = FiscalDocumentStatus.Authorized;
        AuthorizedAt = now;
        UpdatedAt = now;
    }

    public void AttachFiles(string? xmlStorageKey, string? danfeStorageKey, DateTimeOffset now)
    {
        XmlStorageKey = xmlStorageKey ?? XmlStorageKey;
        DanfeStorageKey = danfeStorageKey ?? DanfeStorageKey;
        UpdatedAt = now;
    }

    public void Reject(string message, DateTimeOffset now)
    {
        RequireProcessing();
        Message = Trim(message, MessageMaxLength) ?? "Rejeitada sem motivo informado.";
        Status = FiscalDocumentStatus.Rejected;
        UpdatedAt = now;
    }

    /// <summary>Pelo provedor: só autorizada e dentro do prazo. Externa: só retira o registro (a nota se cancela no emissor dela).</summary>
    public bool CanCancel(DateTimeOffset now) =>
        Status == FiscalDocumentStatus.Authorized && (Source == FiscalDocumentSource.External || now - AuthorizedAt <= CancelWindow);

    public static string NormalizeJustification(string? justification)
    {
        var text = string.Join(' ', (justification ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (text.Length is < JustificationMinLength or > JustificationMaxLength)
            throw new ArgumentException($"Justificativa de {JustificationMinLength} a {JustificationMaxLength} caracteres.", nameof(justification));
        return text;
    }

    public void Cancel(string justification, DateTimeOffset now)
    {
        var text = NormalizeJustification(justification);
        if (Status != FiscalDocumentStatus.Authorized) throw new InvalidOperationException("Só NF-e autorizada pode ser cancelada.");
        if (!CanCancel(now))
            throw new InvalidOperationException("Passou o prazo de 24 horas para cancelar esta NF-e. Para desfazer a venda, emita uma NF-e de devolução.");
        Status = FiscalDocumentStatus.Canceled;
        CancelJustification = text;
        CanceledAt = now;
        UpdatedAt = now;
    }

    private void RequireProcessing()
    {
        if (Status != FiscalDocumentStatus.Processing) throw new InvalidOperationException("A NF-e não está em processamento.");
    }

    private static string? Trim(string? text, int max)
    {
        var value = text?.Trim();
        return string.IsNullOrEmpty(value) ? null : value.Length > max ? value[..max] : value;
    }
}
