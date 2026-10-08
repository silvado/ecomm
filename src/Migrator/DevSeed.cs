using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Platform;
using Ecommerce.Infrastructure.Identity;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Ecommerce.Migrator;

/// <summary>
/// Só desenvolvimento: cria a loja <c>demo</c> (demo.localhost) com um Dono, para entrar no painel antes de existir
/// o cadastro de lojas (RF01). Ligado apenas se <c>DevSeed__OwnerEmail</c> e <c>DevSeed__OwnerPassword</c> existirem.
/// Idempotente: não faz nada se a loja já existir.
/// </summary>
internal static partial class DevSeed
{
    private const string Slug = "demo";

    public static async Task RunAsync(IHost host)
    {
        var config = host.Services.GetRequiredService<IConfiguration>();
        var email = config["DevSeed:OwnerEmail"];
        var password = config["DevSeed:OwnerPassword"];
        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password)) return;

        var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DevSeed));
        if (!PasswordPolicy.IsValid(password))
        {
            LogWeakPassword(logger, PasswordPolicy.MinLength, PasswordPolicy.MaxLength);
            return;
        }

        await using var scope = host.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PlatformDbContext>();
        if (await db.Tenants.AnyAsync(t => t.Slug == Slug)) return;

        var now = DateTimeOffset.UtcNow;
        var tenant = Tenant.Create(Slug, Cnpj.Parse("11222333000181"), "Loja Demo Ltda", "Loja Demo", "localhost", now);
        tenant.Activate();
        var user = await db.UserAccounts.SingleOrDefaultAsync(u => u.Email == UserAccount.NormalizeEmail(email))
            ?? UserAccount.Create(email, scope.ServiceProvider.GetRequiredService<PasswordHashing>().Hash(password), mustChangePassword: false, now);
        if (db.Entry(user).State == EntityState.Detached) db.UserAccounts.Add(user);
        db.Tenants.Add(tenant);
        db.TenantMemberships.Add(new TenantMembership(tenant.Id, user.Id, TenantRole.Owner, now));
        await db.SaveChangesAsync();
        LogSeeded(logger, Slug, user.Id);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "DevSeed: senha fora da política ({Min} a {Max} caracteres); semente ignorada.")]
    private static partial void LogWeakPassword(ILogger logger, int min, int max);

    [LoggerMessage(Level = LogLevel.Information, Message = "DevSeed: loja {Slug} criada com o Dono {UserId}.")]
    private static partial void LogSeeded(ILogger logger, string slug, Guid userId);
}