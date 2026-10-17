using System.Net;
using Ecommerce.Infrastructure.Channels.MercadoLivre;

namespace Ecommerce.Integration.Tests.Channels;

/// <summary>Leitura dos avisos do ML no formato de https://developers.mercadolivre.com.br/pt_br/produto-receba-notificacoes.</summary>
public sealed class MercadoLivreNotificationReadTests
{
    private static readonly MercadoLivreOptions App = new() { ClientId = "5503910054141466" };

    [Fact]
    public void Topico_geral_com_user_id_numero_e_chave_pelo_recurso()
    {
        var (check, n) = MercadoLivreNotifications.Read("""
            {"resource":"/orders/2195160686","user_id":468424240,"topic":"orders_v2","application_id":5503910054141466,"attempts":1,
             "sent":"2019-10-30T16:19:20.129Z","received":"2019-10-30T16:19:20.106Z"}
            """, IPAddress.Loopback, App);

        Assert.Equal(NotificationCheck.Valid, check);
        Assert.Equal(("468424240", "orders_v2", "/orders/2195160686", "orders_v2|/orders/2195160686|2019-10-30T16:19:20.106Z"),
            (n!.UserId, n.Topic, n.Resource, n.IdempotencyKey));
    }

    [Fact]
    public void Topico_com_id_usa_o_id_como_chave_e_aceita_user_id_texto()
    {
        var (check, n) = MercadoLivreNotifications.Read("""
            {"_id":"f9f08571-1f65-4c46-9e0a-c0f43faa1557e","resource":"/items/MLA686791111","user_id":"123456789","topic":"items",
             "application_id":5503910054141466,"attempts":1,"sent":"2025-01-21T13:44:33.006Z","received":"2025-01-21T13:44:32.984Z"}
            """, null, App);

        Assert.Equal(NotificationCheck.Valid, check);
        Assert.Equal(("123456789", "f9f08571-1f65-4c46-9e0a-c0f43faa1557e"), (n!.UserId, n.IdempotencyKey));
    }

    [Fact]
    public void Aviso_de_outro_app_e_recusado()
    {
        var (check, _) = MercadoLivreNotifications.Read("""{"resource":"/orders/1","user_id":1,"topic":"orders_v2","application_id":999}""", null, App);

        Assert.Equal(NotificationCheck.Forbidden, check);
    }

    [Fact]
    public void Com_lista_de_IPs_so_aceita_os_do_ML()
    {
        var options = new MercadoLivreOptions { ClientId = "5503910054141466", NotificationIps = ["54.88.218.97"] };
        const string body = """{"resource":"/orders/1","user_id":1,"topic":"orders_v2","application_id":5503910054141466}""";

        Assert.Equal(NotificationCheck.Valid, MercadoLivreNotifications.Read(body, IPAddress.Parse("54.88.218.97").MapToIPv6(), options).Check);
        Assert.Equal(NotificationCheck.Forbidden, MercadoLivreNotifications.Read(body, IPAddress.Parse("203.0.113.9"), options).Check);
        Assert.Equal(NotificationCheck.Forbidden, MercadoLivreNotifications.Read(body, null, options).Check);
    }

    [Theory]
    [InlineData("/items/MLB123456", "MLB123456")]
    [InlineData("/items/MLB123456/description", "MLB123456")]
    [InlineData("/orders/123", null)]
    [InlineData("/items/../x", null)]
    public void Id_do_anuncio_no_recurso(string resource, string? expected)
    {
        Assert.Equal(expected, MercadoLivreNotifications.ItemId(resource));
    }
}
