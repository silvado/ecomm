using System.Net.Http.Json;
using System.Text.Json.Serialization;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Ecommerce.Application.Email;
using Ecommerce.Domain.Email;
using Ecommerce.Infrastructure.Email;
using MimeKit;

namespace Ecommerce.Integration.Tests.Email;

/// <summary>Mailpit real (mesma imagem do compose de dev): servidor SMTP que guarda as mensagens e as mostra por API.</summary>
public sealed class MailpitFixture : IAsyncLifetime
{
    private IContainer _container = null!;

    public string Host => _container.Hostname;
    public int SmtpPort => _container.GetMappedPublicPort(1025);
    public HttpClient Api { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _container = new ContainerBuilder(Environment.GetEnvironmentVariable("TEST_MAILPIT_IMAGE") ?? "axllent/mailpit:v1.27.0")
            .WithPortBinding(1025, true)
            .WithPortBinding(8025, true)
            .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(r => r.ForPort(8025).ForPath("/livez")))
            .Build();
        await _container.StartAsync();
        Api = new HttpClient { BaseAddress = new Uri($"http://{_container.Hostname}:{_container.GetMappedPublicPort(8025)}") };
    }

    public async Task DisposeAsync()
    {
        Api.Dispose();
        await _container.DisposeAsync();
    }
}

/// <summary>Contrato do envio por SMTP (MailKit) contra um servidor real: remetente, resposta, assunto e as duas versões do corpo.</summary>
public sealed class SmtpEmailSenderTests(MailpitFixture mailpit) : IClassFixture<MailpitFixture>
{
    private sealed record Mailbox(string Name, string Address);
    private sealed record Summary(string ID, Mailbox From, List<Mailbox> To, string Subject);
    private sealed record Page(List<Summary> Messages);
    private sealed record Message(string Text, [property: JsonPropertyName("HTML")] string Html, List<Mailbox> ReplyTo);

    private SmtpEmailSender.Route Route(string? host = null, int? port = null) => new("smtp-plataforma", host ?? mailpit.Host, port ?? mailpit.SmtpPort,
        SmtpSecurity.None, null, null, new MailboxAddress("Loja Teste", "nao-responda@plataforma.test"), new MailboxAddress("Loja Teste", "contato@loja.test"));

    [Fact]
    public async Task Entrega_ao_servidor_com_remetente_resposta_e_corpos_html_e_texto()
    {
        var to = $"{Guid.NewGuid():N}@comprador.test";
        await SmtpEmailSender.DeliverAsync(Route(), new EmailMessage(to, "Maria", "Pedido nº 7 recebido — Loja Teste", "<p>Olá <b>Maria</b></p>", "Olá Maria"),
            CancellationToken.None);

        var page = (await mailpit.Api.GetFromJsonAsync<Page>($"/api/v1/search?query={Uri.EscapeDataString("to:" + to)}"))!;
        var summary = Assert.Single(page.Messages);
        Assert.Equal(("Loja Teste", "nao-responda@plataforma.test"), (summary.From.Name, summary.From.Address));
        Assert.Equal(to, Assert.Single(summary.To).Address);
        Assert.Equal("Pedido nº 7 recebido — Loja Teste", summary.Subject);

        var message = (await mailpit.Api.GetFromJsonAsync<Message>($"/api/v1/message/{summary.ID}"))!;
        Assert.Equal("contato@loja.test", Assert.Single(message.ReplyTo).Address);
        Assert.Contains("<b>Maria</b>", message.Html, StringComparison.Ordinal);
        Assert.Contains("Olá Maria", message.Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Servidor_fora_do_ar_vira_falha_de_entrega_sem_credencial_na_mensagem()
    {
        var secret = "segredo-" + Guid.NewGuid().ToString("N"); // gerado: nada de senha literal no repositório
        var route = Route(port: 1) with { User = "usuario@loja.test", Password = secret };
        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            SmtpEmailSender.DeliverAsync(route, new EmailMessage("a@b.test", null, "x", "<p>x</p>", "x"), CancellationToken.None));
        Assert.DoesNotContain(secret, error.Message, StringComparison.Ordinal);
        Assert.Contains("Não foi possível conectar", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sem_servidor_configurado_falha_sem_tentar_conectar()
    {
        var error = await Assert.ThrowsAsync<EmailDeliveryException>(() =>
            SmtpEmailSender.DeliverAsync(Route(host: ""), new EmailMessage("a@b.test", null, "x", "<p>x</p>", "x"), CancellationToken.None));
        Assert.Equal("Servidor de e-mail não configurado.", error.Message);
    }
}
