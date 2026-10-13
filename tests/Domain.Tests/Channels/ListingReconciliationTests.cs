using Ecommerce.Domain.Channels;

namespace Ecommerce.Domain.Tests.Channels;

/// <summary>RF27: situação esperada no canal segundo a verdade local, e o resultado da conferência no anúncio.</summary>
public sealed class ListingReconciliationTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    private static ChannelListing Listing(int quantity = 2)
    {
        var listing = new ChannelListing(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now);
        listing.Published("SIM-1", quantity, 100m, Now);
        return listing;
    }

    [Theory]
    [InlineData(3, ListingStatus.Active)]
    [InlineData(0, ListingStatus.PausedBySystem)]
    public void Esperado_segue_o_saldo(int available, ListingStatus expected) => Assert.Equal(expected, Listing().ExpectedStatus(available));

    [Fact]
    public void Pausa_do_lojista_vale_com_qualquer_saldo()
    {
        var listing = Listing();
        listing.PauseByUser(Now);
        Assert.Equal(ListingStatus.PausedByUser, listing.ExpectedStatus(10));
        listing.Reconciled(ListingStatus.PausedByUser, 10, 100m, corrected: true, Now.AddHours(1));
        Assert.Equal((ListingStatus.PausedByUser, Now.AddHours(1)), (listing.Status, listing.LastReconciledAt));
    }

    [Fact]
    public void Conferencia_ajusta_a_situacao_local_e_limpa_o_erro()
    {
        var listing = Listing();
        listing.Failed("canal fora do ar", Now);
        listing.Reconciled(listing.ExpectedStatus(4), 4, 90m, corrected: false, Now.AddHours(1));
        Assert.Equal((ListingStatus.Active, 4, 90m, (string?)null), (listing.Status, listing.LastPushedQuantity, listing.LastPushedPrice, listing.LastError));
    }

    [Fact]
    public void Sumido_do_canal_fica_em_erro()
    {
        var listing = Listing();
        listing.MissingInChannel(Now);
        Assert.Equal(ListingStatus.Error, listing.Status);
        Assert.Contains("não existe mais", listing.LastError, StringComparison.Ordinal);
        Assert.Equal(Now, listing.LastReconciledAt);
    }
}
