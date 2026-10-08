using Microsoft.AspNetCore.Identity;

namespace Ecommerce.Infrastructure.Identity;

/// <summary>
/// Hash de senha do painel (RF07 CA4): PBKDF2-HMAC-SHA512 do ASP.NET Core Identity (formato V3, sal por senha).
/// <see cref="PasswordVerificationResult.SuccessRehashNeeded"/> avisa quando os parâmetros ficaram fracos.
/// </summary>
public sealed class PasswordHashing
{
    // A implementação padrão não usa o usuário; o tipo genérico só existe para a interface do Identity.
    private readonly PasswordHasher<object> _hasher = new();
    private static readonly object NoUser = new();

    public string Hash(string password) => _hasher.HashPassword(NoUser, password);

    public PasswordVerificationResult Verify(string hash, string password) => _hasher.VerifyHashedPassword(NoUser, hash, password);
}
