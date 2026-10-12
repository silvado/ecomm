using Ecommerce.Domain.Customers;
using Ecommerce.Domain.Email;
using Ecommerce.Domain.Inventory;

namespace Ecommerce.Domain.Tests.Customers;

/// <summary>RF16 e Q38: tokens do acesso do comprador, sessão e configuração do e-mail de envio da loja.</summary>
public sealed class CustomerAccessTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 12, 12, 0, 0, TimeSpan.Zero);
    private static readonly Guid Tenant = Guid.CreateVersion7();

    [Fact]
    public void Token_tem_43_caracteres_base64url_e_cada_um_e_diferente()
    {
        var tokens = Enumerable.Range(0, 50).Select(_ => CustomerEmail.NewToken()).ToList();
        Assert.All(tokens, t => Assert.Matches("^[A-Za-z0-9_-]{43}$", t));
        Assert.Equal(50, tokens.Distinct().Count());
        Assert.Equal(32, CustomerEmail.HashOrNull(tokens[0])!.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("curto")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Token_fora_do_formato_nem_vira_hash(string? token) => Assert.Null(CustomerEmail.HashOrNull(token));

    [Fact]
    public void Link_vale_15_minutos_e_guarda_o_email_normalizado()
    {
        var link = new CustomerAccessLink(Tenant, "  Maria@Exemplo.COM.br ", CustomerEmail.HashOrNull(CustomerEmail.NewToken())!, Now);
        Assert.Equal("maria@exemplo.com.br", link.Email);
        Assert.Equal(Now.AddMinutes(15), link.ExpiresAt);
        Assert.Null(link.UsedAt);
    }

    [Fact]
    public void Sessao_dura_30_dias_e_sair_encerra()
    {
        var session = new CustomerSession(Tenant, "maria@exemplo.com.br", new byte[32], Now);
        Assert.True(session.IsActive(Now.AddDays(29)));
        Assert.False(session.IsActive(Now.AddDays(30)));

        session.End(Now.AddHours(1));
        session.End(Now.AddHours(2));
        Assert.Equal(Now.AddHours(1), session.EndedAt);
        Assert.False(session.IsActive(Now.AddHours(1)));
    }

    [Fact]
    public void Hash_com_tamanho_errado_e_recusado() =>
        Assert.Throws<ArgumentException>(() => new CustomerSession(Tenant, "a@b.com", new byte[16], Now));

    [Fact]
    public void Smtp_da_loja_e_normalizado()
    {
        var settings = new StoreSettings(Tenant);
        settings.ConfigureEmail(" Contato@Loja.com.br ", new SmtpServer(" SMTP.Gmail.com ", 587, SmtpSecurity.StartTls, " contato@loja.com.br ", "  Loja   Exemplo ", "Contato@Loja.com.br"));

        Assert.Equal("contato@loja.com.br", settings.ContactEmail);
        Assert.Equal(new SmtpServer("smtp.gmail.com", 587, SmtpSecurity.StartTls, "contato@loja.com.br", "Loja Exemplo", "contato@loja.com.br"), settings.Smtp);

        settings.ConfigureEmail(null, null);
        Assert.Null(settings.ContactEmail);
        Assert.Null(settings.Smtp);
    }

    [Theory]
    [InlineData("smtp gmail.com", 587, "u", "Loja", "a@b.com")]
    [InlineData("smtp.gmail.com", 0, "u", "Loja", "a@b.com")]
    [InlineData("smtp.gmail.com", 587, "", "Loja", "a@b.com")]
    [InlineData("smtp.gmail.com", 587, "u", "", "a@b.com")]
    [InlineData("smtp.gmail.com", 587, "u", "Loja", "sem-arroba")]
    [InlineData("smtp.gmail.com", 587, "u", "Loja", "a@localhost")]
    public void Smtp_invalido_e_recusado(string host, int port, string user, string name, string address) =>
        Assert.Throws<ArgumentException>(() => StoreEmailRules.Normalize(new SmtpServer(host, port, SmtpSecurity.StartTls, user, name, address)));

    [Fact]
    public void Email_de_contato_invalido_e_recusado()
    {
        var settings = new StoreSettings(Tenant);
        Assert.Throws<ArgumentException>(() => settings.ConfigureEmail("contato", null));
        Assert.Null(settings.ContactEmail);
    }
}
