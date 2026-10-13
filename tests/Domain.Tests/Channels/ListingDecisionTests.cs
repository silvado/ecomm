using Ecommerce.Domain.Channels;

namespace Ecommerce.Domain.Tests.Channels;

/// <summary>RF24: o que o anúncio faz no canal conforme o saldo e o preço atuais.</summary>
public sealed class ListingDecisionTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static ChannelListing Listing(int quantity = 2, decimal price = 100m)
    {
        var listing = new ChannelListing(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now);
        listing.Published("SIM-1", quantity, price, Now);
        return listing;
    }

    [Fact]
    public void Ativo_que_zera_pausa_e_volta_sozinho_quando_tem_saldo()
    {
        var listing = Listing();
        Assert.Equal(ListingAction.Pause, listing.Decide(0, 100m));
        listing.Applied(ListingAction.Pause, 0, 100m, Now);
        Assert.Equal(ListingStatus.PausedBySystem, listing.Status);

        Assert.Equal(ListingAction.None, listing.Decide(0, 100m));
        Assert.Equal(ListingAction.Resume, listing.Decide(3, 100m));
        listing.Applied(ListingAction.Resume, 3, 100m, Now);
        Assert.Equal((ListingStatus.Active, 3), (listing.Status, listing.LastPushedQuantity));
    }

    [Fact]
    public void Ativo_so_atualiza_quando_quantidade_ou_preco_mudam()
    {
        var listing = Listing(2, 100m);
        Assert.Equal(ListingAction.None, listing.Decide(2, 100m));
        Assert.Equal(ListingAction.UpdateQuantity, listing.Decide(1, 100m));
        Assert.Equal(ListingAction.UpdateQuantity, listing.Decide(2, 90m));
    }

    [Fact]
    public void Pausado_pelo_lojista_nao_volta_sozinho()
    {
        var listing = Listing();
        listing.PauseByUser(Now);
        Assert.Equal(ListingAction.None, listing.Decide(5, 100m));
        Assert.Equal(ListingAction.None, listing.Decide(0, 100m));

        listing.ResumeByUser(0, 100m, Now);
        Assert.Equal(ListingStatus.PausedBySystem, listing.Status); // retomou sem saldo: fica com o sistema
        Assert.Equal(ListingAction.Resume, listing.Decide(1, 100m));
    }

    [Fact]
    public void Erro_tenta_de_novo_na_proxima_mudanca()
    {
        var listing = Listing();
        listing.Failed("canal fora do ar", Now);
        Assert.Equal(ListingAction.UpdateQuantity, listing.Decide(2, 100m));
        Assert.Equal(ListingAction.Pause, listing.Decide(0, 100m));
        listing.Applied(ListingAction.UpdateQuantity, 2, 100m, Now);
        Assert.Null(listing.LastError);
    }

    [Fact]
    public void Publicado_sem_saldo_nasce_pausado_pelo_sistema_e_encerrado_nao_faz_nada()
    {
        Assert.Equal(ListingStatus.PausedBySystem, Listing(0).Status);
        var pending = new ChannelListing(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now);
        Assert.Equal(ListingAction.None, pending.Decide(5, 100m));
        Assert.Throws<InvalidOperationException>(() => pending.PauseByUser(Now));
    }
}
