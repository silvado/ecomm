using Ecommerce.Application.Common;

namespace Ecommerce.Application.Tests.Common;

/// <summary>Redação de dados sensíveis em logs e no log de integração (RNF05, RF29 CA1).</summary>
public sealed class RedactionTests
{
    [Theory]
    [InlineData("Authorization: Bearer APP_USR-1234567890abcdef-112233-xyz", "Authorization: ***")]
    [InlineData("chamada com Bearer APP_USR-1234567890abcdef-112233-xyz", "Bearer ***")]
    [InlineData("access_token=APP_USR-123456789&x=1", "access_token=***")]
    [InlineData("{\"client_secret\": \"s3cr3t-valor\"}", "\"client_secret\": \"***")]
    [InlineData("senha: minhaSenha123", "senha: ***")]
    [InlineData("CPF 123.456.789-09 não confere", "CPF *** não confere")]
    [InlineData("loja 11.222.333/0001-81", "loja ***")]
    [InlineData("comprador maria@exemplo.com.br", "comprador ***")]
    public void Dados_sensiveis_viram_mascara(string input, string expected)
    {
        var redacted = Redaction.Text(input)!;
        Assert.Contains(expected, redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("APP_USR", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void Jwt_vira_mascara()
    {
        // Montado em tempo de execução: um literal com cara de token dispara o scanner de segredos (gitleaks).
        var jwt = string.Join(".", "eyJ" + "hbGciOiJIUzI1NiJ9", "eyJ" + "zdWIiOiIxMjM0NTY3ODkwIn0", "c2lnbmF0dXJh" + "LWZha2UtMTIz");
        Assert.Equal("token ***", Redaction.Text($"token {jwt}"));
    }

    [Theory]
    [InlineData("Ecommerce.Infrastructure.Integrations.IntegrationLog")]
    [InlineData("Envelope #01a12c06-f13f-7856-9835-303dd4203517 (StockChanged)")]
    [InlineData("correlação e23caf145600502c8b906f871bbea474")]
    public void Namespaces_e_guids_nao_sao_mascarados(string text) => Assert.Equal(text, Redaction.Text(text));

    [Fact]
    public void Texto_comum_passa_e_e_cortado_no_limite()
    {
        Assert.Equal("Mercado Pago respondeu 503.", Redaction.Text("Mercado Pago respondeu 503."));
        Assert.Equal(10, Redaction.Text(new string('a', 50), 10)!.Length);
        Assert.Null(Redaction.Text(null));
    }

    [Theory]
    [InlineData("Password", true)]
    [InlineData("access_token", true)]
    [InlineData("ClientSecret", true)]
    [InlineData("BuyerCpf", true)]
    [InlineData("OrderId", false)]
    [InlineData("Channel", false)]
    public void Nomes_de_campos_sensiveis_sao_reconhecidos(string name, bool sensitive) =>
        Assert.Equal(sensitive, Redaction.IsSensitiveName(name));

    [Fact]
    public void Correlacao_externa_e_saneada()
    {
        Assert.Equal("abc-123", Correlation.Sanitize("abc-123\r\nINJECT"[..7]));
        Assert.Equal("abc123INJECT", Correlation.Sanitize("abc123\r\nINJECT"));
        Assert.Equal(Correlation.MaxLength, Correlation.Sanitize(new string('x', 200))!.Length);
        Assert.Null(Correlation.Sanitize("  "));
    }
}
