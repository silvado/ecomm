using System.Globalization;
using System.Net;
using Ecommerce.Application.Email;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Vault;
using Ecommerce.Infrastructure.Email;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Wolverine;

namespace Ecommerce.Infrastructure.Vault;

/// <summary>
/// Aviso de vencimento do certificado A1 por e-mail aos Donos (RF06 CA5), na transação do Wolverine: uma linha em
/// <c>certificate_reminders</c> por certificado e dia de aviso (índice único — reiniciar o Worker no mesmo dia não manda de
/// novo) e um e-mail por Dono pela fila. Sem opção de desligar: sem certificado válido, a NF-e para.
/// </summary>
public static class CertificateReminderHandler
{
    public static async Task<CertificateReminderOutcome> Handle(SendCertificateReminder command, TenantDbContext db, EmailBranding branding,
        IOptions<PlatformOptions> platform, IMessageBus bus, TimeProvider clock, CancellationToken ct)
    {
        var owners = command.Owners.Select(o => o.Email.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (owners.Count == 0) return CertificateReminderOutcome.Skipped;

        // O certificado pode ter sido trocado entre a conferência e agora: só avisa sobre o que está no cofre.
        var current = await db.TenantSecrets.AsNoTracking()
            .Where(s => s.Kind == SecretKind.A1Certificate && s.Name == VaultPanel.CertificateName).Select(s => s.ExpiresAt).SingleOrDefaultAsync(ct);
        if (current != command.ExpiresAt) return CertificateReminderOutcome.Skipped;
        if (await db.CertificateReminders.AnyAsync(r => r.CertificateExpiresAt == command.ExpiresAt && r.DaysLeft == command.DaysLeft, ct))
            return CertificateReminderOutcome.Skipped;

        var tenantId = db.RequireTenantId();
        var brand = await branding.ForCurrentStoreAsync(ct);
        var panel = platform.Value.PanelUrl is { Length: > 0 } url ? url : null;
        // Duas rodadas ao mesmo tempo: a segunda esbarra no índice único e, na nova tentativa, vê o aviso já enviado.
        db.CertificateReminders.Add(new CertificateReminder(tenantId, command.ExpiresAt, command.DaysLeft, owners.Count, clock.GetUtcNow()));
        foreach (var owner in owners)
            await bus.PublishAsync(new SendCertificateReminderEmail(CertificateReminderEmail.Build(brand, owner, command.DaysLeft, command.ExpiresAt, panel)),
                new DeliveryOptions { TenantId = tenantId.ToString() });
        return CertificateReminderOutcome.Sent;
    }

    public static Task Handle(SendCertificateReminderEmail command, IEmailSender sender, CancellationToken ct) =>
        sender.SendAsync(command.Message, "aviso do certificado digital", null, ct);
}

/// <summary>E-mail do aviso do certificado ao Dono, no mesmo estilo dos outros e-mails da loja.</summary>
public static class CertificateReminderEmail
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly TimeZoneInfo StoreTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static EmailMessage Build(EmailBrand brand, string to, int daysLeft, DateTimeOffset expiresAt, string? panelUrl)
    {
        ArgumentNullException.ThrowIfNull(brand);
        var e = (string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        var color = brand.PrimaryColor.StartsWith('#') && brand.PrimaryColor.Length == 7 ? brand.PrimaryColor : "#1F5FBF";
        var subject = CertificateReminderTexts.Subject(daysLeft, brand.StoreName);
        var headline = CertificateReminderTexts.Headline(daysLeft);
        var when = TimeZoneInfo.ConvertTime(expiresAt, StoreTimeZone).ToString("dd/MM/yyyy 'às' HH:mm", Invariant);
        var validity = daysLeft < 0 ? $"Venceu em {when}." : $"Validade: {when}.";
        var link = panelUrl is { Length: > 0 } ? panelUrl.TrimEnd('/') + "/cofre" : null;

        var html = $"""
            <!doctype html><html lang="pt-BR"><body style="margin:0;background:#f4f5f7;font-family:Arial,Helvetica,sans-serif;color:#1d2330">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:24px 12px">
            <table role="presentation" width="100%" style="max-width:560px;background:#ffffff;border-radius:8px" cellpadding="0" cellspacing="0">
            <tr><td style="background:{color};color:#ffffff;padding:16px 24px;font-size:18px;font-weight:bold;border-radius:8px 8px 0 0">{e(brand.StoreName)}</td></tr>
            <tr><td style="padding:24px"><h1 style="font-size:20px;margin:0 0 16px">Certificado digital</h1>
            <p style="margin:0 0 12px;line-height:1.5">{e(headline)}</p>
            <p style="margin:0 0 12px;line-height:1.5">{e(validity)}</p>
            <p style="margin:0 0 12px;line-height:1.5"><strong>O que fazer:</strong> {e(CertificateReminderTexts.Action)}</p>
            {(link is null ? string.Empty : $"""<p style="margin:24px 0"><a href="{e(link)}" style="background:{color};color:#ffffff;padding:12px 20px;border-radius:6px;text-decoration:none;display:inline-block">Abrir o Cofre</a></p>""")}
            <p style="margin:16px 0 0;font-size:12px;color:#667085">Você recebe este aviso por ser Dono da loja. Ele sai 30, 15 e 5 dias antes do vencimento, no dia e no dia seguinte.</p>
            </td></tr></table></td></tr></table></body></html>
            """;
        var text = $"{brand.StoreName}\n\n{headline}\n{validity}\n\nO que fazer: {CertificateReminderTexts.Action}\n"
            + (link is null ? string.Empty : $"\nAbrir o Cofre: {link}\n")
            + "\nEste aviso sai 30, 15 e 5 dias antes do vencimento, no dia e no dia seguinte.\n";
        return new EmailMessage(to, null, subject, html, text);
    }
}
