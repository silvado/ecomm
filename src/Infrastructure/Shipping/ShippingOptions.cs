namespace Ecommerce.Infrastructure.Shipping;

/// <summary>Configuração de frete da plataforma (seção <c>Shipping</c>).</summary>
public sealed class ShippingOptions
{
    public const string Section = "Shipping";

    /// <summary>Dev/testes: lojas sem Melhor Envio conectado usam o provedor fake (tabela simples).</summary>
    public bool UseFakeProvider { get; set; }

    public MelhorEnvioOptions MelhorEnvio { get; set; } = new();
}

/// <summary>
/// Melhor Envio. <see cref="BaseUrl"/>: produção <c>https://melhorenvio.com.br</c>, sandbox
/// <c>https://sandbox.melhorenvio.com.br</c>. <see cref="UserAgent"/> é obrigatório pela API: nome do aplicativo e
/// e-mail de contato técnico. Fonte: https://docs.melhorenvio.com.br/reference/calculo-de-fretes-por-produtos
/// </summary>
public sealed class MelhorEnvioOptions
{
    public string BaseUrl { get; set; } = "https://sandbox.melhorenvio.com.br";
    public string UserAgent { get; set; } = string.Empty;
}
