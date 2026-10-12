using System.Text;
using Ecommerce.Application.Email;
using Ecommerce.Application.Integrations;
using Ecommerce.Application.Tenancy;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Email;
using Ecommerce.Domain.Vault;
using Ecommerce.Infrastructure.Integrations;
using Ecommerce.Infrastructure.Persistence;
using MailKit;
using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Ecommerce.Infrastructure.Email;

/// <summary>
/// Remetente padrão da plataforma (Q38): SMTP configurado por variável de ambiente — Amazon SES, Resend ou outro em
/// produção, Mailpit em desenvolvimento. Sem <see cref="Host"/>, não há envio (falha registrada).
/// </summary>
public sealed class EmailOptions
{
    public const string Section = "Email:Platform";

    /// <summary>"smtp" (padrão) ou "memory" (testes: <see cref="InMemoryEmailSender"/>).</summary>
    public string Provider { get; set; } = "smtp";
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public SmtpSecurity Security { get; set; } = SmtpSecurity.StartTls;
    public string? User { get; set; }
    public string? Password { get; set; }
    public string FromAddress { get; set; } = "nao-responda@localhost";
    public string FromName { get; set; } = "Loja";
}

/// <summary>
/// E-mail da loja do escopo por SMTP (MailKit): o servidor da loja (senha no cofre) ou o remetente padrão da plataforma,
/// com o nome da loja e resposta para o contato dela. Toda tentativa vai para o log de integração (RF29).
/// </summary>
public sealed class SmtpEmailSender(TenantDbContext db, ISecretVault vault, ITenantCatalog catalog, ITenantContext tenant, IIntegrationLog log,
    IOptions<EmailOptions> platform) : IEmailSender
{
    public const string PasswordSecretName = "smtp:password";
    public const string StoreChannel = "smtp-loja";
    public const string PlatformChannel = "smtp-plataforma";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    public async Task SendAsync(EmailMessage message, string purpose, Guid? orderId = null, CancellationToken ct = default)
    {
        var route = await RouteAsync(ct);
        await Logged.RunAsync(log, route.Channel, $"enviar e-mail ({purpose})", async () =>
        {
            await DeliverAsync(route, message, ct);
            return true;
        }, _ => Mask(message.To), orderId, null, ct);
    }

    /// <summary>Servidor, credenciais e remetente desta loja agora.</summary>
    internal async Task<Route> RouteAsync(CancellationToken ct)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("Envio de e-mail sem loja no escopo.");
        var settings = await db.StoreSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        var storeName = (await catalog.FindByIdAsync(tenantId, ct))?.TradeName ?? platform.Value.FromName;

        if (settings?.Smtp is { } smtp)
        {
            var password = await vault.GetAsync(SecretKind.ApiKey, PasswordSecretName, ct);
            return new Route(StoreChannel, smtp.Host, smtp.Port, smtp.Security, smtp.User, password is null ? null : Encoding.UTF8.GetString(password),
                new MailboxAddress(smtp.SenderName, smtp.SenderAddress), null);
        }
        var options = platform.Value;
        return new Route(PlatformChannel, options.Host, options.Port, options.Security, options.User, options.Password,
            new MailboxAddress(storeName, options.FromAddress), settings?.ContactEmail is { } contact ? new MailboxAddress(storeName, contact) : null);
    }

    internal static async Task DeliverAsync(Route route, EmailMessage message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(route.Host)) throw new EmailDeliveryException("Servidor de e-mail não configurado.");
        var mime = new MimeMessage();
        mime.From.Add(route.From);
        if (route.ReplyTo is not null) mime.ReplyTo.Add(route.ReplyTo);
        mime.To.Add(new MailboxAddress(message.ToName ?? string.Empty, message.To));
        mime.Subject = message.Subject;
        mime.Body = new BodyBuilder { HtmlBody = message.Html, TextBody = message.Text }.ToMessageBody();

        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        try
        {
            var security = route.Security switch
            {
                SmtpSecurity.SslOnConnect => SecureSocketOptions.SslOnConnect,
                SmtpSecurity.None => SecureSocketOptions.None,
                _ => SecureSocketOptions.StartTls,
            };
            await client.ConnectAsync(route.Host, route.Port, security, ct);
            if (!string.IsNullOrEmpty(route.User) && !string.IsNullOrEmpty(route.Password))
                await client.AuthenticateAsync(route.User, route.Password, ct);
            await client.SendAsync(mime, ct);
            await client.DisconnectAsync(quit: true, ct);
        }
        catch (Exception e) when (e is SmtpCommandException or SmtpProtocolException or AuthenticationException or IOException
            or System.Net.Sockets.SocketException or SslHandshakeException or ServiceNotConnectedException or TimeoutException)
        {
            // Mensagem sem credencial: só o tipo de falha e o servidor.
            throw new EmailDeliveryException(e switch
            {
                AuthenticationException => $"O servidor {route.Host} recusou o usuário ou a senha do e-mail.",
                SmtpCommandException command => $"O servidor {route.Host} recusou o envio ({(int)command.StatusCode}).",
                SslHandshakeException => $"Falha de segurança (TLS) ao conectar em {route.Host}:{route.Port}.",
                _ => $"Não foi possível conectar ao servidor de e-mail {route.Host}:{route.Port}.",
            }, e);
        }
    }

    /// <summary>"m***@exemplo.com.br" — o log de integração não guarda o e-mail inteiro do comprador.</summary>
    private static string Mask(string address)
    {
        var at = address.IndexOf('@', StringComparison.Ordinal);
        return at <= 0 ? "***" : $"{address[0]}***{address[at..]}";
    }

    internal sealed record Route(string Channel, string Host, int Port, SmtpSecurity Security, string? User, string? Password, MailboxAddress From,
        MailboxAddress? ReplyTo);
}
