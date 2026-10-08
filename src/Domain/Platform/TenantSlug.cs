using System.Text.RegularExpressions;

namespace Ecommerce.Domain.Platform;

/// <summary>Identificador do tenant no subdomínio da plataforma (RF02 CA2).</summary>
public static partial class TenantSlug
{
    private static readonly HashSet<string> Reserved =
    [
        "www", "api", "admin", "app", "mail", "smtp", "imap", "pop", "ftp", "static", "cdn", "assets",
        "suporte", "ajuda", "status", "blog", "loja", "lojas", "plataforma", "painel", "login", "conta",
    ];

    [GeneratedRegex("^[a-z0-9](?:[a-z0-9-]{1,38})[a-z0-9]$")]
    private static partial Regex Pattern();

    public static bool IsValid(string? slug) =>
        slug is not null && Pattern().IsMatch(slug) && !slug.Contains("--", StringComparison.Ordinal) && !Reserved.Contains(slug);
}
