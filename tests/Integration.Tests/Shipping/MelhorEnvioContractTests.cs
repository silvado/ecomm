using System.Net;
using System.Text;
using System.Text.Json;
using Ecommerce.Application.Shipping;
using Ecommerce.Domain.Shipping;
using Ecommerce.Infrastructure.Shipping;

namespace Ecommerce.Integration.Tests.Shipping;

/// <summary>
/// Contrato do adaptador do Melhor Envio contra um servidor HTTP simulado, no formato da documentação oficial
/// (https://docs.melhorenvio.com.br/reference/calculo-de-fretes-por-produtos). Nada sai para a internet.
/// </summary>
public sealed class MelhorEnvioContractTests
{
    private sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }
        public string? RequestBody { get; private set; }
        public int Calls { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Request = request;
            RequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static readonly ShippingQuoteRequest Request = new(PostalCode.Parse("01310-100"), PostalCode.Parse("88015-200"),
        [new ShippingPackage("peca-1", 40, 30, 60, 2.5m, 350m, 1), new ShippingPackage("peca-2", 10, 10, 15, 0.3m, 49.9m, 2)]);

    private static (MelhorEnvioShippingProvider Provider, StubHandler Handler) Provider(HttpStatusCode status, string body, string userAgent = "Plataforma (dev@exemplo.com.br)")
    {
        var handler = new StubHandler(status, body);
        var options = new MelhorEnvioOptions { BaseUrl = "https://sandbox.melhorenvio.com.br", UserAgent = userAgent };
        return (new MelhorEnvioShippingProvider(new HttpClient(handler), options, "token-da-loja"), handler);
    }

    private const string Quotes = """
        [
          { "id": 1, "name": "PAC", "price": "45.10", "custom_price": "41.90", "delivery_time": 8, "custom_delivery_time": 9,
            "company": { "id": 1, "name": "Correios", "picture": "x" } },
          { "id": 2, "name": "SEDEX", "price": "80.00", "custom_price": "", "delivery_time": 3, "custom_delivery_time": null,
            "company": { "id": 1, "name": "Correios" } },
          { "id": 3, "name": ".Package", "custom_price": "12.00", "error": "Transportadora não atende este trecho.", "company": { "id": 2, "name": "Jadlog" } },
          { "id": 4, "name": "Sem preço", "company": { "id": 3, "name": "Azul" } }
        ]
        """;

    [Fact]
    public async Task Envia_o_pedido_no_formato_da_api()
    {
        var (provider, handler) = Provider(HttpStatusCode.OK, Quotes);

        await provider.QuoteAsync(Request);

        Assert.Equal(HttpMethod.Post, handler.Request!.Method);
        Assert.Equal("https://sandbox.melhorenvio.com.br/api/v2/me/shipment/calculate", handler.Request.RequestUri!.ToString());
        Assert.Equal(("Bearer", "token-da-loja"), (handler.Request.Headers.Authorization!.Scheme, handler.Request.Headers.Authorization.Parameter));
        Assert.Equal("Plataforma (dev@exemplo.com.br)", handler.Request.Headers.UserAgent.ToString());
        Assert.Contains("application/json", handler.Request.Headers.Accept.ToString(), StringComparison.Ordinal);

        using var json = JsonDocument.Parse(handler.RequestBody!);
        Assert.Equal("01310100", json.RootElement.GetProperty("from").GetProperty("postal_code").GetString());
        Assert.Equal("88015200", json.RootElement.GetProperty("to").GetProperty("postal_code").GetString());
        var product = json.RootElement.GetProperty("products")[0];
        Assert.Equal(("peca-1", 40, 30, 60), (product.GetProperty("id").GetString(), product.GetProperty("width").GetInt32(),
            product.GetProperty("height").GetInt32(), product.GetProperty("length").GetInt32()));
        Assert.Equal((2.5m, 350m, 1), (product.GetProperty("weight").GetDecimal(), product.GetProperty("insurance_value").GetDecimal(), product.GetProperty("quantity").GetInt32()));
    }

    [Fact]
    public async Task Usa_o_preco_da_conta_e_descarta_servicos_sem_preco_ou_com_erro()
    {
        var (provider, _) = Provider(HttpStatusCode.OK, Quotes);

        var options = await provider.QuoteAsync(Request);

        Assert.Equal(
            [new ShippingOption("me-1", "Correios", "PAC", 41.90m, 9), new ShippingOption("me-2", "Correios", "SEDEX", 80.00m, 3)],
            options);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "A conexão com o Melhor Envio expirou ou foi revogada.")]
    [InlineData(HttpStatusCode.UnprocessableEntity, "Melhor Envio respondeu 422.")]
    [InlineData(HttpStatusCode.InternalServerError, "Melhor Envio respondeu 500.")]
    public async Task Erros_da_api_viram_falha_do_provedor_sem_o_token(HttpStatusCode status, string message)
    {
        var (provider, _) = Provider(status, """{"message":"erro"}""");

        var error = await Assert.ThrowsAsync<ShippingProviderException>(() => provider.QuoteAsync(Request));

        Assert.Equal(message, error.Message);
        Assert.DoesNotContain("token-da-loja", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sem_user_agent_configurado_nem_chama_a_api()
    {
        var (provider, handler) = Provider(HttpStatusCode.OK, Quotes, userAgent: "");

        await Assert.ThrowsAsync<ShippingProviderException>(() => provider.QuoteAsync(Request));
        Assert.Equal(0, handler.Calls);
    }
}
