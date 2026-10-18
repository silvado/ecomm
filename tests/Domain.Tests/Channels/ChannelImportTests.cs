using Ecommerce.Domain.Channels;

namespace Ecommerce.Domain.Tests.Channels;

/// <summary>RF11 CA2/CA3 — lista em páginas, lotes com progresso e o vínculo do anúncio importado.</summary>
public sealed class ChannelImportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 18, 15, 0, 0, TimeSpan.Zero);

    private static ChannelImport New() => new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);

    [Fact]
    public void Paginas_acumulam_ids_sem_repetir_e_pagina_vazia_passa_a_importar()
    {
        var import = New();
        import.AddPage(["MLB1", "MLB2"], "c1", Now);
        Assert.Equal((ChannelImportStatus.Listing, "c1"), (import.Status, import.Cursor));
        import.AddPage(["MLB2", "MLB3"], "c2", Now);
        import.AddPage([], "c3", Now);
        Assert.Equal((ChannelImportStatus.Processing, 3, (string?)null), (import.Status, import.TotalItems, import.Cursor));
        Assert.Equal(["MLB1", "MLB2"], import.NextBatch(2));
    }

    [Fact]
    public void Conta_sem_anuncios_termina_na_hora()
    {
        var import = New();
        import.AddPage([], null, Now);
        Assert.Equal(ChannelImportStatus.Completed, import.Status);
        Assert.Empty(import.NextBatch(10));
    }

    [Fact]
    public void Acima_do_limite_os_excedentes_ficam_de_fora()
    {
        var import = New();
        import.AddPage(Enumerable.Range(0, ChannelImport.MaxItems + 3).Select(i => $"MLB{i}"), "c", Now);
        Assert.Equal((ChannelImport.MaxItems, true, ChannelImportStatus.Processing), (import.TotalItems, import.Truncated, import.Status));
    }

    [Fact]
    public void Lotes_contam_cada_resultado_e_o_ultimo_conclui()
    {
        var import = New();
        import.AddPage(["A", "B", "C", "D"], null, Now);
        import.RecordBatch([(ChannelImportItemOutcome.Created, null), (ChannelImportItemOutcome.Linked, new ChannelImportIssue("B", "ligado"))], Now);
        Assert.Equal(["C", "D"], import.NextBatch(10));
        import.RecordBatch([(ChannelImportItemOutcome.AlreadyLinked, null), (ChannelImportItemOutcome.Rejected, new ChannelImportIssue("D", "variações"))], Now);
        Assert.Equal((ChannelImportStatus.Completed, 4, 1, 1, 1, 1, 2), (import.Status, import.ProcessedItems, import.CreatedCount, import.LinkedCount,
            import.AlreadyLinkedCount, import.RejectedCount, import.Issues.Count));
        Assert.Throws<InvalidOperationException>(() => import.RecordBatch([(ChannelImportItemOutcome.Created, null)], Now));
    }

    [Fact]
    public void Falha_encerra_e_guarda_o_motivo()
    {
        var import = New();
        import.Fail("O Mercado Livre pediu nova autorização.", Now);
        Assert.Equal((ChannelImportStatus.Failed, "O Mercado Livre pediu nova autorização."), (import.Status, import.FailureReason));
        Assert.Throws<InvalidOperationException>(() => import.AddPage(["A"], null, Now));
    }

    [Fact]
    public void Anuncio_importado_guarda_o_que_esta_no_canal_e_vale_o_estoque_da_loja()
    {
        var listing = new ChannelListing(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        listing.Imported("MLB123", paused: false, channelQuantity: 5, channelPrice: 180m, Now);
        Assert.Equal((ListingStatus.Active, "MLB123"), (listing.Status, listing.ExternalId));
        Assert.Equal(ListingAction.UpdateQuantity, listing.Decide(1, 180m)); // a loja tem 1: o canal é corrigido
        Assert.Throws<InvalidOperationException>(() => listing.Imported("MLB123", false, 1, 1m, Now));

        var paused = new ChannelListing(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Now);
        paused.Imported("MLB456", paused: true, channelQuantity: 3, channelPrice: 120m, Now);
        Assert.Equal((ListingStatus.PausedByUser, ListingAction.None), (paused.Status, paused.Decide(3, 120m))); // o sistema não reativa
    }
}
