using Ecommerce.Application.Channels;
using Ecommerce.Infrastructure.Channels;

namespace Ecommerce.Integration.Tests.Channels;

/// <summary>
/// Contrato comum dos conectores de canal (requisitos §5). Cada conector real (Mercado Livre, Meta, OLX) herda esta
/// classe apontando para o servidor simulado do canal e precisa passar em tudo: publicar, atualizar, pausar, reativar e
/// ler o estado remoto refletem a mesma coisa.
/// </summary>
public abstract class ChannelConnectorContract
{
    protected abstract IChannelConnector CreateConnector();

    private static ListingDraft Draft(int quantity = 2, decimal price = 150m) =>
        new(Guid.CreateVersion7(), "K-" + Guid.NewGuid().ToString("N")[..6], "Farol dianteiro esquerdo", "Usado, sem trincas.", price, quantity);

    [Fact]
    public async Task Publicar_cria_anuncio_ativo_com_preco_e_quantidade()
    {
        var connector = CreateConnector();
        var id = await connector.PublishAsync(Draft(3, 199.9m));
        Assert.False(string.IsNullOrWhiteSpace(id));
        var remote = await connector.GetRemoteStateAsync(id);
        Assert.Equal((RemoteListingStatus.Active, 3, 199.9m), (remote!.Status, remote.Quantity, remote.Price));
    }

    [Fact]
    public async Task Atualizar_preco_e_quantidade_reflete_no_estado_remoto()
    {
        var connector = CreateConnector();
        var id = await connector.PublishAsync(Draft());
        await connector.UpdatePriceAndQuantityAsync(id, 120m, 1);
        var remote = await connector.GetRemoteStateAsync(id);
        Assert.Equal((1, 120m), (remote!.Quantity, remote.Price));
    }

    [Fact]
    public async Task Pausar_e_reativar_alternam_o_estado_remoto()
    {
        var connector = CreateConnector();
        var id = await connector.PublishAsync(Draft());
        await connector.PauseAsync(id);
        Assert.Equal(RemoteListingStatus.Paused, (await connector.GetRemoteStateAsync(id))!.Status);
        await connector.ResumeAsync(id, 4);
        var remote = await connector.GetRemoteStateAsync(id);
        Assert.Equal((RemoteListingStatus.Active, 4), (remote!.Status, remote.Quantity));
    }

    [Fact]
    public async Task Pausar_duas_vezes_nao_falha()
    {
        var connector = CreateConnector();
        var id = await connector.PublishAsync(Draft());
        await connector.PauseAsync(id);
        await connector.PauseAsync(id);
        Assert.Equal(RemoteListingStatus.Paused, (await connector.GetRemoteStateAsync(id))!.Status);
    }

    [Fact]
    public async Task Anuncio_inexistente_tem_estado_nulo() =>
        Assert.Null(await CreateConnector().GetRemoteStateAsync("NAO-EXISTE-" + Guid.NewGuid().ToString("N")));
}

/// <summary>O conector simulado cumpre o contrato (é a referência dos testes de propagação).</summary>
public sealed class FakeChannelConnectorContractTests : ChannelConnectorContract
{
    protected override IChannelConnector CreateConnector() => new FakeChannelConnector();
}
