using System.Text;
using Ecommerce.Application.Shipping;
using Ecommerce.Application.Vault;
using Ecommerce.Domain.Vault;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Shipping;

/// <summary>
/// Loja com o Melhor Envio conectado (token no cofre, RF06) cota por ele; sem conexão, o fake em dev/testes ou nenhum
/// (a loja só oferece retirada, se habilitada).
/// </summary>
public sealed class ShippingProviderResolver(ISecretVault vault, IHttpClientFactory httpClients, IOptions<ShippingOptions> options) : IShippingProviderResolver
{
    /// <summary>Nome do segredo com o access token OAuth do Melhor Envio da loja.</summary>
    public const string MelhorEnvioTokenName = "melhor-envio:access-token";
    public const string HttpClientName = "melhor-envio";

    public async Task<IShippingProvider?> ResolveAsync(CancellationToken ct = default)
    {
        var token = await vault.GetAsync(SecretKind.OAuthToken, MelhorEnvioTokenName, ct);
        if (token is { Length: > 0 })
            return new MelhorEnvioShippingProvider(httpClients.CreateClient(HttpClientName), options.Value.MelhorEnvio, Encoding.UTF8.GetString(token));
        return options.Value.UseFakeProvider ? new FakeShippingProvider() : null;
    }
}
