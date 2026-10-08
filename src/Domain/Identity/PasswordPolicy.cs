namespace Ecommerce.Domain.Identity;

/// <summary>
/// HIPÓTESE (requisitos → Em aberto): mínimo de 10 caracteres, sem regras de composição (NIST SP 800-63B §3.1.1.2).
/// O máximo evita custo de hash abusivo.
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    public static bool IsValid(string? password) =>
        password is not null && password.Length is >= MinLength and <= MaxLength && !string.IsNullOrWhiteSpace(password);
}
