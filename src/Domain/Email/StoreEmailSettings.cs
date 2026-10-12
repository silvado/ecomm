using System.Net.Mail;

namespace Ecommerce.Domain.Email;

public enum SmtpSecurity
{
    /// <summary>Porta 587: conexão aberta que passa a criptografada (padrão do Google Workspace e do Microsoft 365).</summary>
    StartTls,
    /// <summary>Porta 465: criptografada desde o início.</summary>
    SslOnConnect,
    /// <summary>Sem criptografia: só para servidor na rede interna (ex.: captura de e-mails em desenvolvimento).</summary>
    None,
}

/// <summary>Servidor de saída configurado pela loja (Q38). A senha fica no cofre (RF06), nunca aqui.</summary>
public sealed record SmtpServer(string Host, int Port, SmtpSecurity Security, string User, string SenderName, string SenderAddress);

/// <summary>
/// E-mail da loja ao comprador (RF16, Q38): pelo SMTP da própria loja (Google Workspace, Microsoft 365 ou outro) ou, sem
/// configuração, pelo remetente padrão da plataforma com resposta para o <see cref="ContactEmail"/> da loja.
/// </summary>
public static class StoreEmailRules
{
    public const int HostMaxLength = 255;
    public const int NameMaxLength = 80;
    public const int AddressMaxLength = 254;

    /// <summary>Endereço válido, minúsculo; nulo se vazio.</summary>
    public static string? NormalizeAddress(string? value, string field)
    {
        var text = (value ?? string.Empty).Trim().ToLowerInvariant();
        if (text.Length == 0) return null;
        if (text.Length > AddressMaxLength || !MailAddress.TryCreate(text, out var parsed) || parsed.Address != text || !parsed.Host.Contains('.', StringComparison.Ordinal))
            throw new ArgumentException($"{field}: informe um e-mail válido.", nameof(value));
        return text;
    }

    public static SmtpServer Normalize(SmtpServer server)
    {
        ArgumentNullException.ThrowIfNull(server);
        var host = (server.Host ?? string.Empty).Trim().ToLowerInvariant();
        if (host.Length is 0 or > HostMaxLength || host.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '.' or '-')))
            throw new ArgumentException("Servidor SMTP inválido (ex.: smtp.gmail.com).", nameof(server));
        if (server.Port is < 1 or > 65535) throw new ArgumentException("Porta SMTP inválida (587 ou 465, normalmente).", nameof(server));
        if (!Enum.IsDefined(server.Security)) throw new ArgumentException("Segurança da conexão inválida.", nameof(server));
        var user = (server.User ?? string.Empty).Trim();
        if (user.Length is 0 or > AddressMaxLength) throw new ArgumentException("Informe o usuário do SMTP (normalmente o e-mail).", nameof(server));
        var name = string.Join(' ', (server.SenderName ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (name.Length is 0 or > NameMaxLength || name.Any(char.IsControl)) throw new ArgumentException($"Nome do remetente obrigatório, até {NameMaxLength} caracteres.", nameof(server));
        var address = NormalizeAddress(server.SenderAddress, "Remetente") ?? throw new ArgumentException("Informe o e-mail do remetente.", nameof(server));
        return new SmtpServer(host, server.Port, server.Security, user, name, address);
    }
}
