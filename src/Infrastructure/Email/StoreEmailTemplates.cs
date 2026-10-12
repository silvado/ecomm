using System.Globalization;
using System.Net;
using System.Text;
using Ecommerce.Application.Email;
using Ecommerce.Domain.Orders;

namespace Ecommerce.Infrastructure.Email;

/// <summary>Marca da loja nos e-mails: nome, cor principal e endereço (https://{loja}.{plataforma}).</summary>
public sealed record EmailBrand(string StoreName, string PrimaryColor, string Origin);

/// <summary>
/// E-mails da loja ao comprador (RF16, Q39), em HTML simples (tabelas e estilo em linha, como os leitores de e-mail
/// exigem) e em texto. Todo dado do pedido é escapado: nome, título de peça e motivo vêm de fora.
/// </summary>
public static class StoreEmailTemplates
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly TimeZoneInfo StoreTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");

    public static EmailMessage AccessLink(EmailBrand brand, string to, string link) => Build(brand, to, null,
        $"Seu acesso aos pedidos — {brand.StoreName}",
        "Acesse seus pedidos",
        [
            "Recebemos um pedido para acessar a área do cliente com este e-mail.",
            "O link vale por 15 minutos e só pode ser usado uma vez. Se não foi você, ignore este e-mail.",
        ],
        ("Ver meus pedidos", link), null);

    public static EmailMessage Test(EmailBrand brand, string to) => Build(brand, to, null,
        $"Teste de e-mail — {brand.StoreName}",
        "O e-mail da loja está funcionando",
        ["Esta é uma mensagem de teste enviada pelo painel. Os avisos de pedido aos compradores sairão por este mesmo caminho."],
        ("Abrir a loja", brand.Origin), null);

    public static EmailMessage Order(EmailBrand brand, CustomerOrder order, OrderEmail kind, string link)
    {
        var number = order.Number.ToString(Invariant);
        var (subject, title, lines) = kind switch
        {
            OrderEmail.Placed => ($"Pedido nº {number} recebido — {brand.StoreName}", $"Recebemos seu pedido nº {number}",
                new[] { $"As peças ficam reservadas para você até {When(order.PaymentDeadline)}, aguardando o pagamento.", "Pelo link abaixo você paga e acompanha o pedido." }),
            OrderEmail.Paid => ($"Pagamento confirmado — pedido nº {number}", "Pagamento confirmado", new[]
            {
                "Obrigado pela compra!",
                order.DeliveryMethod == DeliveryMethod.Pickup
                    ? $"As peças já podem ser retiradas: {order.PickupAddress}"
                    : "Avisaremos quando o pedido for enviado.",
            }),
            OrderEmail.Shipped => ($"Pedido nº {number} enviado", "Seu pedido foi enviado", order.TrackingCode is { } code
                ? new[] { $"Código de rastreio: {code} ({order.ShippingCarrier} · {order.ShippingService})." }
                : new[] { $"Enviado por {order.ShippingCarrier} · {order.ShippingService}." }),
            OrderEmail.Canceled => ($"Pedido nº {number} cancelado", "Pedido cancelado", new[] { order.CancelReason ?? "O pedido foi cancelado." }),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
        };
        return Build(brand, order.BuyerEmail, order.BuyerName, subject, title, lines, ("Acompanhar o pedido", link), order);
    }

    private static EmailMessage Build(EmailBrand brand, string to, string? toName, string subject, string title, IReadOnlyList<string> lines,
        (string Label, string Url) action, CustomerOrder? order)
    {
        var e = (string? s) => WebUtility.HtmlEncode(s ?? string.Empty);
        var color = brand.PrimaryColor.StartsWith('#') && brand.PrimaryColor.Length == 7 ? brand.PrimaryColor : "#1F5FBF";
        var html = new StringBuilder();
        html.Append(CultureInfo.InvariantCulture, $"""
            <!doctype html><html lang="pt-BR"><body style="margin:0;background:#f4f5f7;font-family:Arial,Helvetica,sans-serif;color:#1d2330">
            <table role="presentation" width="100%" cellpadding="0" cellspacing="0"><tr><td align="center" style="padding:24px 12px">
            <table role="presentation" width="100%" style="max-width:560px;background:#ffffff;border-radius:8px" cellpadding="0" cellspacing="0">
            <tr><td style="background:{color};color:#ffffff;padding:16px 24px;font-size:18px;font-weight:bold;border-radius:8px 8px 0 0">{e(brand.StoreName)}</td></tr>
            <tr><td style="padding:24px"><h1 style="font-size:20px;margin:0 0 16px">{e(title)}</h1>
            """);
        foreach (var line in lines) html.Append(CultureInfo.InvariantCulture, $"""<p style="margin:0 0 12px;line-height:1.5">{e(line)}</p>""");
        if (order is not null)
        {
            html.Append("""<table role="presentation" width="100%" cellpadding="0" cellspacing="0" style="margin:16px 0;border-top:1px solid #d6dae1">""");
            foreach (var item in order.Items)
                html.Append(CultureInfo.InvariantCulture, $"""<tr><td style="padding:8px 0;border-bottom:1px solid #d6dae1">{item.Quantity} × {e(item.Title)}</td><td align="right" style="padding:8px 0;border-bottom:1px solid #d6dae1">{Money(item.UnitPrice * item.Quantity)}</td></tr>""");
            if (order.ShippingTotal > 0)
                html.Append(CultureInfo.InvariantCulture, $"""<tr><td style="padding:8px 0">Frete</td><td align="right" style="padding:8px 0">{Money(order.ShippingTotal)}</td></tr>""");
            html.Append(CultureInfo.InvariantCulture, $"""<tr><td style="padding:8px 0;font-weight:bold">Total</td><td align="right" style="padding:8px 0;font-weight:bold">{Money(order.Total)}</td></tr></table>""");
        }
        html.Append(CultureInfo.InvariantCulture, $"""
            <p style="margin:24px 0"><a href="{e(action.Url)}" style="background:{color};color:#ffffff;padding:12px 20px;border-radius:6px;text-decoration:none;display:inline-block">{e(action.Label)}</a></p>
            <p style="margin:0;font-size:12px;color:#667085">Se o botão não abrir, copie este endereço no navegador:<br>{e(action.Url)}</p>
            </td></tr></table></td></tr></table></body></html>
            """);

        var text = new StringBuilder().AppendLine(brand.StoreName).AppendLine().AppendLine(title).AppendLine();
        foreach (var line in lines) text.AppendLine(line);
        if (order is not null)
        {
            text.AppendLine();
            foreach (var item in order.Items) text.AppendLine(CultureInfo.InvariantCulture, $"{item.Quantity} x {item.Title}: {Money(item.UnitPrice * item.Quantity)}");
            if (order.ShippingTotal > 0) text.AppendLine(CultureInfo.InvariantCulture, $"Frete: {Money(order.ShippingTotal)}");
            text.AppendLine(CultureInfo.InvariantCulture, $"Total: {Money(order.Total)}");
        }
        text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"{action.Label}: {action.Url}");
        return new EmailMessage(to, toName, subject, html.ToString(), text.ToString());
    }

    // Sem depender da cultura instalada no contêiner: "R$ 1.234,56".
    private static string Money(decimal value) =>
        "R$ " + value.ToString("#,##0.00", Invariant).Replace(",", "\u0001", StringComparison.Ordinal).Replace(".", ",", StringComparison.Ordinal).Replace("\u0001", ".", StringComparison.Ordinal);

    private static string When(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, StoreTimeZone).ToString("dd/MM/yyyy 'às' HH:mm", Invariant);
}
