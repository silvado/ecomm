using Ecommerce.Application.Email;
using Ecommerce.Application.Tenancy;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Platform;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Email;

/// <summary>Nome, cor e endereço da loja do escopo para os e-mails (endereço no subdomínio da plataforma, que sempre aponta para a loja).</summary>
public sealed class EmailBranding(TenantDbContext db, ITenantCatalog catalog, ITenantContext tenant, IOptions<PlatformOptions> platform)
{
    public async Task<EmailBrand> ForCurrentStoreAsync(CancellationToken ct)
    {
        var tenantId = tenant.TenantId ?? throw new InvalidOperationException("E-mail sem loja no escopo.");
        var store = await catalog.FindByIdAsync(tenantId, ct) ?? throw new InvalidOperationException("Loja não encontrada.");
        var color = await db.StoreBrandings.AsNoTracking().Select(b => b.PrimaryColor).SingleOrDefaultAsync(ct) ?? "#1F5FBF";
        var domain = platform.Value.Domain is { Length: > 0 } d ? d : "localhost";
        return new EmailBrand(store.TradeName, color, $"https://{store.Slug}.{domain}");
    }
}

/// <summary>
/// E-mails da loja ao comprador, pela fila (RF16, Q39): falha do servidor de e-mail é tentada de novo (RF28) e cada
/// tentativa aparece no log de integração (RF29). Pedido sem e-mail (balcão) não recebe nada.
/// </summary>
public static class EmailHandler
{
    public static async Task Handle(NotifyBuyer command, TenantDbContext db, EmailBranding branding, IEmailSender sender, CancellationToken ct)
    {
        var order = await db.CustomerOrders.AsNoTracking().Include(o => o.Items).SingleOrDefaultAsync(o => o.Id == command.OrderId, ct);
        if (order is null || string.IsNullOrEmpty(order.BuyerEmail)) return;

        var brand = await branding.ForCurrentStoreAsync(ct);
        // Só o e-mail do pedido recebido tem o link secreto do pedido; os demais levam à área do cliente (pede o link por e-mail).
        var link = command.AccessToken is { } token ? $"{brand.Origin}/pedido#{token}" : $"{brand.Origin}/meus-pedidos";
        var message = StoreEmailTemplates.Order(brand, order, command.Email, link);
        await sender.SendAsync(message, Purpose(command.Email), order.Id, ct);
    }

    public static async Task Handle(SendCustomerAccessLink command, EmailBranding branding, IEmailSender sender, CancellationToken ct)
    {
        var brand = await branding.ForCurrentStoreAsync(ct);
        var origin = Uri.TryCreate(command.Origin, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" ? uri.GetLeftPart(UriPartial.Authority) : brand.Origin;
        await sender.SendAsync(StoreEmailTemplates.AccessLink(brand, command.Email, $"{origin}/meus-pedidos#{command.Token}"), "link de acesso", null, ct);
    }

    private static string Purpose(OrderEmail kind) => kind switch
    {
        OrderEmail.Placed => "pedido recebido",
        OrderEmail.Paid => "pagamento confirmado",
        OrderEmail.Shipped => "pedido enviado",
        OrderEmail.Canceled => "pedido cancelado",
        _ => kind.ToString(),
    };
}
