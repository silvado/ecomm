using System.Globalization;
using System.Net;
using System.Text;
using Ecommerce.Application.Email;
using Ecommerce.Application.Reports;
using Ecommerce.Infrastructure.Email;

namespace Ecommerce.Infrastructure.Reports;

/// <summary>
/// E-mail do resumo diário ao Dono (RF34), no mesmo estilo dos e-mails da loja: abertura da IA (se aceita) e, sempre, os
/// números das consultas em tabela. Títulos de peça vêm do cadastro e são escapados.
/// </summary>
public static class DailySummaryEmail
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static EmailMessage Build(EmailBrand brand, string to, DailySummaryFacts facts, string? opening, string? panelUrl)
    {
        ArgumentNullException.ThrowIfNull(brand);
        ArgumentNullException.ThrowIfNull(facts);
        var e = (string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        var color = brand.PrimaryColor.StartsWith('#') && brand.PrimaryColor.Length == 7 ? brand.PrimaryColor : "#1F5FBF";
        var day = facts.Day.ToString("dd/MM/yyyy", Invariant);
        var subject = $"Resumo de {day} — {brand.StoreName}";
        const string cell = "padding:6px 0;border-bottom:1px solid #d6dae1";

        var html = new StringBuilder();
        var text = new StringBuilder().AppendLine(brand.StoreName).AppendLine().AppendLine(Invariant, $"Resumo de {day}").AppendLine();
        html.Append(Invariant, $"""
            <!doctype html><html lang="pt-BR"><body style="margin:0;background:#f4f5f7;font-family:Arial,Helvetica,sans-serif;color:#1d2330">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:24px 12px">
            <table role="presentation" width="100%" style="max-width:560px;background:#ffffff;border-radius:8px" cellpadding="0" cellspacing="0">
            <tr><td style="background:{color};color:#ffffff;padding:16px 24px;font-size:18px;font-weight:bold;border-radius:8px 8px 0 0">{e(brand.StoreName)}</td></tr>
            <tr><td style="padding:24px"><h1 style="font-size:20px;margin:0 0 16px">Resumo de {day}</h1>
            """);
        if (opening is { Length: > 0 })
        {
            html.Append(Invariant, $"""<p style="margin:0 0 16px;line-height:1.5">{e(opening)}</p>""");
            text.AppendLine(opening).AppendLine();
        }

        // Vendas de ontem por canal.
        html.Append("""<h2 style="font-size:16px;margin:16px 0 8px">Vendas de ontem</h2>""");
        text.AppendLine("Vendas de ontem");
        if (facts.Sales.Count == 0)
        {
            html.Append("""<p style="margin:0 0 12px">Nenhuma venda paga ontem.</p>""");
            text.AppendLine("Nenhuma venda paga ontem.");
        }
        else
        {
            html.Append("""<table role="presentation" width="100%" cellpadding="0" cellspacing="0">""");
            foreach (var s in facts.Sales)
            {
                html.Append(Invariant, $"""<tr><td style="{cell}">{e(DailySummaryRules.OriginLabel(s.Origin))}</td><td align="right" style="{cell}">{Count(s.Orders, "pedido", "pedidos")}</td><td align="right" style="{cell}">{Money(s.Total)}</td></tr>""");
                text.AppendLine(Invariant, $"- {DailySummaryRules.OriginLabel(s.Origin)}: {Count(s.Orders, "pedido", "pedidos")}, {Money(s.Total)}");
            }
            html.Append(Invariant, $"""<tr><td style="padding:6px 0;font-weight:bold">Total</td><td align="right" style="padding:6px 0;font-weight:bold">{Count(facts.Orders, "pedido", "pedidos")}</td><td align="right" style="padding:6px 0;font-weight:bold">{Money(facts.Total)}</td></tr></table>""");
            text.AppendLine(Invariant, $"Total: {Count(facts.Orders, "pedido", "pedidos")}, {Money(facts.Total)}");
        }

        // Perguntas sem resposta (RF31).
        var questions = facts.PendingQuestions == 0
            ? "Nenhuma pergunta de comprador esperando resposta."
            : $"{Count(facts.PendingQuestions, "pergunta de comprador espera", "perguntas de compradores esperam")} resposta"
              + (facts.OldestPendingQuestion is { } oldest ? $" (a mais antiga de {TimeZoneInfo.ConvertTime(oldest, DailySummaryFactsQuery.StoreTimeZone).ToString("dd/MM 'às' HH:mm", Invariant)})." : ".");
        html.Append(Invariant, $"""<h2 style="font-size:16px;margin:20px 0 8px">Perguntas</h2><p style="margin:0 0 12px">{e(questions)}</p>""");
        text.AppendLine().AppendLine("Perguntas").AppendLine(questions);

        // Peças paradas.
        var stale = facts.StaleParts == 0
            ? $"Nenhuma peça com estoque parada há mais de {DailySummaryFacts.StaleDays} dias."
            : $"{Count(facts.StaleParts, "peça com estoque está", "peças com estoque estão")} sem venda há mais de {DailySummaryFacts.StaleDays} dias.";
        html.Append(Invariant, $"""<h2 style="font-size:16px;margin:20px 0 8px">Peças paradas</h2><p style="margin:0 0 8px">{e(stale)}</p>""");
        text.AppendLine().AppendLine("Peças paradas").AppendLine(stale);
        if (facts.StaleSample.Count > 0)
        {
            html.Append("""<table role="presentation" width="100%" cellpadding="0" cellspacing="0">""");
            foreach (var p in facts.StaleSample)
            {
                html.Append(Invariant, $"""<tr><td style="{cell}">{e(p.InternalCode)} · {e(p.Title)}</td><td align="right" style="{cell}">{p.DaysWithoutSale} dias</td></tr>""");
                text.AppendLine(Invariant, $"- {p.InternalCode} · {p.Title}: {p.DaysWithoutSale} dias");
            }
            html.Append("</table>");
        }

        if (panelUrl is { Length: > 0 })
        {
            html.Append(Invariant, $"""<p style="margin:24px 0"><a href="{e(panelUrl)}" style="background:{color};color:#ffffff;padding:12px 20px;border-radius:6px;text-decoration:none;display:inline-block">Abrir o painel</a></p>""");
            text.AppendLine().AppendLine(Invariant, $"Abrir o painel: {panelUrl}");
        }
        html.Append("""
            <p style="margin:16px 0 0;font-size:12px;color:#667085">Você recebe este resumo por ser Dono da loja. Para mudar o horário ou desligar: Dados da loja → Resumo diário.</p>
            </td></tr></table></td></tr></table></body></html>
            """);
        text.AppendLine().AppendLine("Para mudar o horário ou desligar: Dados da loja → Resumo diário.");
        return new EmailMessage(to, null, subject, html.ToString(), text.ToString());
    }

    private static string Count(int n, string one, string many) => n == 1 ? $"1 {one}" : $"{n.ToString(Invariant)} {many}";

    // Sem depender da cultura instalada no contêiner: "R$ 1.234,56".
    internal static string Money(decimal value) =>
        "R$ " + value.ToString("#,##0.00", Invariant).Replace(",", "\u0001", StringComparison.Ordinal).Replace(".", ",", StringComparison.Ordinal).Replace("\u0001", ".", StringComparison.Ordinal);
}
