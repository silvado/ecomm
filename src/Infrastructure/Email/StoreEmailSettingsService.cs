using System.Text;
using Ecommerce.Application.Email;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Email;
using Ecommerce.Domain.Vault;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Email;

/// <summary>
/// E-mail de envio da loja (Q38), configurado pelo Dono em Dados da loja: contato para as respostas e, opcionalmente, o
/// SMTP da própria loja (Google Workspace, Microsoft 365 ou outro) com a senha no cofre (RF06).
/// </summary>
public sealed class StoreEmailSettingsService(TenantDbContext db, ISecretVault vault, IEmailSender sender, EmailBranding branding,
    IOptions<EmailOptions> platform) : IStoreEmailSettings
{
    public const int PasswordMaxLength = 200;

    public async Task<StoreEmailSettingsView> GetAsync(CancellationToken ct = default)
    {
        var settings = await db.StoreSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        var hasPassword = await HasPasswordAsync(ct);
        var smtp = settings?.Smtp;
        return new StoreEmailSettingsView(settings?.ContactEmail, smtp is not null, smtp?.Host, smtp?.Port, smtp?.Security, smtp?.User,
            smtp?.SenderName, smtp?.SenderAddress, hasPassword, platform.Value.FromAddress);
    }

    public async Task<StoreEmailSettingsView> SaveAsync(SaveStoreEmailSettings request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var password = string.IsNullOrEmpty(request.Password) ? null : request.Password;
        if (password is { Length: > PasswordMaxLength } || password?.Any(char.IsControl) == true)
            throw new ArgumentException("Senha do SMTP inválida.", nameof(request));
        if (request.UseOwnSmtp && request.Smtp is null) throw new ArgumentException("Informe os dados do servidor SMTP.", nameof(request));

        var tenantId = db.RequireTenantId();
        await using (var transaction = await db.Database.BeginTransactionAsync(ct))
        {
            await db.Database.ExecuteSqlAsync($"""
                INSERT INTO store_settings (tenant_id, reservation_minutes) VALUES ({tenantId}, {Domain.Inventory.StoreSettings.DefaultReservationMinutes})
                ON CONFLICT (tenant_id) DO NOTHING
                """, ct);
            var settings = await db.StoreSettings.SingleAsync(ct);
            settings.ConfigureEmail(request.ContactEmail, request.UseOwnSmtp ? request.Smtp : null);
            if (settings.Smtp is not null && password is null && !await HasPasswordAsync(ct))
                throw new ArgumentException("Informe a senha do SMTP (no Google Workspace, uma senha de app).", nameof(request));
            await db.SaveChangesAsync(ct);

            // Sem SMTP próprio, a senha antiga não fica esquecida no cofre.
            if (settings.Smtp is null) await vault.DeleteAsync(SecretKind.ApiKey, SmtpEmailSender.PasswordSecretName, ct);
            else if (password is not null) await vault.SetAsync(SecretKind.ApiKey, SmtpEmailSender.PasswordSecretName, Encoding.UTF8.GetBytes(password), ct: ct);
            await transaction.CommitAsync(ct);
        }
        return await GetAsync(ct);
    }

    public async Task<EmailTestResult> SendTestAsync(string recipient, CancellationToken ct = default)
    {
        string address;
        try
        {
            address = StoreEmailRules.NormalizeAddress(recipient,"Destinatário") ?? throw new ArgumentException("Informe o e-mail para o teste.");
        }
        catch (ArgumentException e)
        {
            return new EmailTestResult(EmailTestOutcome.Invalid, e.Message.Split(" (Parameter", 2)[0]);
        }

        try
        {
            await sender.SendAsync(StoreEmailTemplates.Test(await branding.ForCurrentStoreAsync(ct), address), "teste de e-mail", null, ct);
            return new EmailTestResult(EmailTestOutcome.Sent);
        }
        catch (EmailDeliveryException e)
        {
            return new EmailTestResult(EmailTestOutcome.Failed, e.Message);
        }
    }

    private async Task<bool> HasPasswordAsync(CancellationToken ct) =>
        (await vault.ListAsync(ct)).Any(s => s.Kind == SecretKind.ApiKey && s.Name == SmtpEmailSender.PasswordSecretName);
}
