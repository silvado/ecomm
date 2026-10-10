using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ecommerce.Application.Shipping;

namespace Ecommerce.Infrastructure.Shipping;

/// <summary>
/// Cotação no Melhor Envio (RF14 CA1) com o token OAuth da loja (escopo <c>shipping-calculate</c>, guardado no cofre).
/// <c>POST {base}/api/v2/me/shipment/calculate</c> com <c>from</c>/<c>to</c> (CEP) e <c>products</c> (cm, kg, BRL).
/// Usa <c>custom_price</c>/<c>custom_delivery_time</c>, que já trazem descontos e taxas da conta; serviços sem preço
/// (indisponíveis para a rota/volume) ficam de fora.
/// Fonte: https://docs.melhorenvio.com.br/reference/calculo-de-fretes-por-produtos
/// </summary>
public sealed class MelhorEnvioShippingProvider(HttpClient http, MelhorEnvioOptions options, string accessToken) : IShippingProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        NumberHandling = JsonNumberHandling.AllowReadingFromString,
    };

    private sealed record Address(string PostalCode);
    private sealed record Product(string Id, int Width, int Height, int Length, decimal Weight, decimal InsuranceValue, int Quantity);
    private sealed record CalculateRequest(Address From, Address To, IReadOnlyList<Product> Products);
    private sealed record Company(int Id, string? Name);
    private sealed record Quote(int Id, string? Name, string? CustomPrice, string? Price, int? CustomDeliveryTime, int? DeliveryTime, Company? Company, string? Error);

    public async Task<IReadOnlyList<ShippingOption>> QuoteAsync(ShippingQuoteRequest request, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(options.UserAgent))
            throw new ShippingProviderException("Melhor Envio sem User-Agent configurado (Shipping:MelhorEnvio:UserAgent).");

        var body = new CalculateRequest(new Address(request.From.Value), new Address(request.To.Value),
            request.Packages.Select(p => new Product(p.Id, p.WidthCm, p.HeightCm, p.LengthCm, p.WeightKg, decimal.Round(p.InsuranceValue, 2), p.Quantity)).ToList());

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.BaseUrl), "/api/v2/me/shipment/calculate"))
        {
            Content = JsonContent.Create(body, options: Json),
        };
        message.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        message.Headers.TryAddWithoutValidation("User-Agent", options.UserAgent);

        HttpResponseMessage response;
        try { response = await http.SendAsync(message, ct); }
        catch (HttpRequestException e) { throw new ShippingProviderException("Melhor Envio indisponível.", e); }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                throw new ShippingProviderException("A conexão com o Melhor Envio expirou ou foi revogada.");
            if (!response.IsSuccessStatusCode)
                throw new ShippingProviderException($"Melhor Envio respondeu {(int)response.StatusCode}.");

            var quotes = await response.Content.ReadFromJsonAsync<List<Quote>>(Json, ct) ?? [];
            return quotes
                .Where(q => q.Error is null && Price(q) is not null)
                .Select(q => new ShippingOption($"me-{q.Id}", q.Company?.Name ?? "Transportadora", q.Name ?? $"Serviço {q.Id}",
                    Price(q)!.Value, q.CustomDeliveryTime ?? q.DeliveryTime ?? 0))
                .OrderBy(o => o.Price)
                .ToList();
        }
    }

    private static decimal? Price(Quote quote)
    {
        var text = string.IsNullOrWhiteSpace(quote.CustomPrice) ? quote.Price : quote.CustomPrice;
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var price) && price > 0 ? price : null;
    }
}
