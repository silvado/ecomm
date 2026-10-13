using Ecommerce.Domain.Integrations;
using Ecommerce.Domain.Platform;

namespace Ecommerce.Domain.Tests.Integrations;

/// <summary>RF26: aviso de canal no inbox e mapa de contas externas.</summary>
public sealed class InboxEventTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static InboxEvent Event() => new(" canal ", " chave-1 ", "vendedor-9", "pedido", "/orders/1", "{}", Now);

    [Fact]
    public void Nasce_pendente_sem_loja_e_normalizado()
    {
        var e = Event();
        Assert.Equal(("canal", "chave-1", InboxStatus.Pending, (Guid?)null), (e.Source, e.IdempotencyKey, e.Status, e.TenantId));
    }

    [Fact]
    public void Roteia_uma_vez_so()
    {
        var e = Event();
        var tenant = Guid.CreateVersion7();
        e.Route(tenant, Now.AddSeconds(1));
        Assert.Equal((InboxStatus.Routed, (Guid?)tenant), (e.Status, e.TenantId));
        Assert.Throws<InvalidOperationException>(() => e.Route(tenant, Now));
        Assert.Throws<InvalidOperationException>(() => e.MarkUnroutable(Now));
    }

    [Fact]
    public void Sem_dono_nao_ganha_loja()
    {
        var e = Event();
        e.MarkUnroutable(Now);
        Assert.Equal((InboxStatus.Unroutable, (Guid?)null), (e.Status, e.TenantId));
    }

    [Theory]
    [InlineData("", "k")]
    [InlineData("canal", "")]
    [InlineData("canal", "linha\nquebrada")]
    public void Campos_invalidos_sao_recusados(string source, string key) =>
        Assert.Throws<ArgumentException>(() => new InboxEvent(source, key, "conta", "topico", "/r", null, Now));

    [Fact]
    public void Aviso_grande_demais_e_recusado() =>
        Assert.Throws<ArgumentException>(() => new InboxEvent("canal", "k", "conta", "topico", "/r", new string('x', InboxEvent.PayloadMaxLength + 1), Now));

    [Fact]
    public void Conta_de_canal_exige_loja() =>
        Assert.Throws<ArgumentException>(() => new ChannelAccount(Guid.Empty, "mercado-livre", "123", Now));
}
