using System.Globalization;
using System.Net;
using System.Text;
using Ecommerce.Application.Email;
using Ecommerce.Application.Reports;
using Ecommerce.Infrastructure.Email;

namespace Ecommerce.Infrastructure.Reports;

/// <summary>
/// E-mail do guardião da sincronização ao Dono (RF33 CA2), no mesmo estilo do resumo diário: para cada problema, o que
/// falhou, a explicação e a ação sugerida. O erro original vem do log (já redigido) e é escapado.
/// </summary>
public static class SyncAlertEmail
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static EmailMessage Build(EmailBrand brand, string to, IReadOnlyList<SyncDiagnosis> diagnoses, string? panelUrl)
    {
        ArgumentNullException.ThrowIfNull(brand);
        ArgumentNullException.ThrowIfNull(diagnoses);
        var e = (string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        var color = brand.PrimaryColor.StartsWith('#') && brand.PrimaryColor.Length == 7 ? brand.PrimaryColor : "#1F5FBF";
        var subject = diagnoses.Count == 1
            ? $"Atenção: problema na sincronização — {brand.StoreName}"
            : $"Atenção: {diagnoses.Count.ToString(Invariant)} problemas na sincronização — {brand.StoreName}";
        var intro = diagnoses.Count == 1
            ? "O guardião da sincronização encontrou um problema que precisa da sua atenção."
            : $"O guardião da sincronização encontrou {diagnoses.Count.ToString(Invariant)} problemas que precisam da sua atenção.";

        var html = new StringBuilder();
        var text = new StringBuilder().AppendLine(brand.StoreName).AppendLine().AppendLine(intro).AppendLine();
        html.Append(Invariant, $"""
            <!doctype html><html lang="pt-BR"><body style="margin:0;background:#f4f5f7;font-family:Arial,Helvetica,sans-serif;color:#1d2330">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:24px 12px">
            <table role="presentation" width="100%" style="max-width:560px;background:#ffffff;border-radius:8px" cellpadding="0" cellspacing="0">
            <tr><td style="background:{color};color:#ffffff;padding:16px 24px;font-size:18px;font-weight:bold;border-radius:8px 8px 0 0">{e(brand.StoreName)}</td></tr>
            <tr><td style="padding:24px"><h1 style="font-size:20px;margin:0 0 16px">Problema na sincronização</h1>
            <p style="margin:0 0 16px;line-height:1.5">{e(intro)}</p>
            """);
        foreach (var d in diagnoses)
        {
            var when = TimeZoneInfo.ConvertTime(d.LastAt, DailySummaryFactsQuery.StoreTimeZone).ToString("dd/MM 'às' HH:mm", Invariant);
            html.Append(Invariant, $"""
                <div style="border-left:4px solid #c2410c;padding:8px 12px;margin:0 0 16px;background:#fff7ed">
                <p style="margin:0 0 6px;font-weight:bold">{e(d.Title)}</p>
                <p style="margin:0 0 6px;line-height:1.5">{e(d.Explanation)}</p>
                <p style="margin:0 0 6px;line-height:1.5"><strong>O que fazer:</strong> {e(d.ActionText)}</p>
                <p style="margin:0;font-size:12px;color:#667085">Última vez: {when}{(d.LastError is { Length: > 0 } err ? " · Erro: " + e(err) : string.Empty)}</p>
                </div>
                """);
            text.AppendLine(d.Title).AppendLine(d.Explanation).AppendLine(Invariant, $"O que fazer: {d.ActionText}").AppendLine(Invariant, $"Última vez: {when}");
            if (d.LastError is { Length: > 0 } error) text.AppendLine(Invariant, $"Erro: {error}");
            text.AppendLine();
        }

        if (panelUrl is { Length: > 0 })
        {
            var link = panelUrl.TrimEnd('/') + "/integracoes";
            html.Append(Invariant, $"""<p style="margin:24px 0"><a href="{e(link)}" style="background:{color};color:#ffffff;padding:12px 20px;border-radius:6px;text-decoration:none;display:inline-block">Abrir Integrações</a></p>""");
            text.AppendLine(Invariant, $"Abrir Integrações: {link}");
        }
        html.Append("""
            <p style="margin:16px 0 0;font-size:12px;color:#667085">Você recebe este aviso por ser Dono da loja. O mesmo problema só é avisado de novo depois de 24 horas, se continuar. Para desligar: Integrações → Guardião da sincronização.</p>
            </td></tr></table></td></tr></table></body></html>
            """);
        text.AppendLine().AppendLine("O mesmo problema só é avisado de novo depois de 24 horas, se continuar. Para desligar: Integrações → Guardião da sincronização.");
        return new EmailMessage(to, null, subject, html.ToString(), text.ToString());
    }
}
