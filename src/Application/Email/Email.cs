using Ecommerce.Domain.Email;

namespace Ecommerce.Application.Email;

/// <summary>Mensagem já pronta (assunto, HTML e texto). Destinatário único: e-mail transacional, nunca em massa.</summary>
public sealed record EmailMessage(string To, string? ToName, string Subject, string Html, string Text);

/// <summary>Falha ao entregar ao servidor SMTP (fora do ar, recusa, credencial): a fila tenta de novo (RF28).</summary>
public sealed class EmailDeliveryException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Envio de e-mail da loja do escopo (Q38): pelo SMTP da loja, se configurado, senão pelo remetente padrão da plataforma
/// (com resposta para o e-mail de contato da loja). Toda tentativa entra no log de integração (RF29).
/// </summary>
public interface IEmailSender
{
    /// <param name="purpose">Para o log de integração, em português (ex.: "link de acesso").</param>
    Task SendAsync(EmailMessage message, string purpose, Guid? orderId = null, CancellationToken ct = default);
}

/// <summary>O que a loja manda ao comprador sobre o pedido (Q39).</summary>
public enum OrderEmail
{
    Placed,
    Paid,
    Shipped,
    Canceled,
}

/// <summary>
/// Aviso ao comprador, pela fila (com novas tentativas). <paramref name="AccessToken"/> só no pedido recebido: o link de
/// acompanhamento, que existe apenas na hora do checkout (o banco guarda o hash).
/// </summary>
public sealed record NotifyBuyer(Guid OrderId, OrderEmail Email, string? AccessToken = null);

/// <summary>Link de acesso à área do cliente, pela fila.</summary>
public sealed record SendCustomerAccessLink(string Email, string Token, string Origin);

public sealed record StoreEmailSettingsView(string? ContactEmail, bool UsesOwnSmtp, string? Host, int? Port, SmtpSecurity? Security, string? User,
    string? SenderName, string? SenderAddress, bool HasPassword, string PlatformSender);

/// <param name="Password">Nulo ou vazio = mantém a senha já salva.</param>
public sealed record SaveStoreEmailSettings(string? ContactEmail, bool UseOwnSmtp, SmtpServer? Smtp, string? Password);

public enum EmailTestOutcome
{
    Sent,
    Invalid,
    Failed,
}

public sealed record EmailTestResult(EmailTestOutcome Outcome, string? Message = null);

/// <summary>Configuração do e-mail de envio da loja (Dono), com teste imediato.</summary>
public interface IStoreEmailSettings
{
    Task<StoreEmailSettingsView> GetAsync(CancellationToken ct = default);

    /// <exception cref="ArgumentException">Dados inválidos (mensagem para o usuário).</exception>
    Task<StoreEmailSettingsView> SaveAsync(SaveStoreEmailSettings request, CancellationToken ct = default);

    /// <summary>Envia agora (sem fila) um e-mail de teste com a configuração salva, para o Dono ver o erro na hora.</summary>
    Task<EmailTestResult> SendTestAsync(string recipient,CancellationToken ct = default);
}
