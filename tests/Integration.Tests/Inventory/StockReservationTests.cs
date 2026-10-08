using Ecommerce.Application.Inventory;
using Ecommerce.Domain.Inventory;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Ecommerce.Integration.Tests.Inventory;

/// <summary>RF12 (reserva atômica) e RNF02 (venda dupla é inaceitável).</summary>
public sealed class StockReservationTests(InventoryFixture fx) : IClassFixture<InventoryFixture>
{
    private Task<ReserveStockResult> ReserveAsync(Guid partId, int quantity = 1, Guid? tenant = null) =>
        fx.InvokeAsync<ReserveStockResult>(tenant ?? fx.TenantA, new ReserveStock(partId, quantity, $"pedido-{Guid.NewGuid():N}"));

    private Task<SellStockResult> SellAsync(Guid partId, int quantity = 1) =>
        fx.InvokeAsync<SellStockResult>(fx.TenantA, new SellStock(partId, quantity, $"balcao-{Guid.NewGuid():N}"));

    private Task<ConfirmReservationOutcome> ConfirmAsync(Guid reservationId) =>
        fx.InvokeAsync<ConfirmReservationOutcome>(fx.TenantA, new ConfirmReservation(reservationId));

    [Fact]
    public async Task Reserva_com_saldo_segura_a_peca_pelo_prazo_padrao()
    {
        var part = await fx.CreatePartAsync(fx.TenantA, onHand: 2);

        var result = await ReserveAsync(part);

        Assert.True(result.Reserved);
        var stock = await fx.GetStockAsync(fx.TenantA, part);
        Assert.Equal((2, 1, 1), (stock.OnHand, stock.Reserved, stock.Available));
        var reservation = await fx.GetReservationAsync(fx.TenantA, result.ReservationId!.Value);
        Assert.Equal(ReservationStatus.Active, reservation.Status);
        Assert.Equal(TimeSpan.FromMinutes(StoreSettings.DefaultReservationMinutes), reservation.ExpiresAt - reservation.CreatedAt);
    }

    [Fact]
    public async Task Reserva_sem_saldo_e_recusada()
    {
        var part = await fx.CreatePartAsync(fx.TenantA, onHand: 1);
        Assert.True((await ReserveAsync(part)).Reserved);

        var second = await ReserveAsync(part);

        Assert.False(second.Reserved);
        Assert.Equal(0, (await fx.GetStockAsync(fx.TenantA, part)).Available);
    }

    [Fact]
    public async Task Cinquenta_reservas_simultaneas_pela_ultima_unidade_resultam_em_exatamente_uma()
    {
        var part = await fx.CreatePartAsync(fx.TenantA, onHand: 1);

        var results = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => Task.Run(() => ReserveAsync(part))));

        Assert.Single(results, r => r.Reserved);
        var stock = await fx.GetStockAsync(fx.TenantA, part);
        Assert.Equal((1, 1, 0), (stock.OnHand, stock.Reserved, stock.Available));
        var active = await fx.QueryAsync(fx.TenantA, db => db.StockReservations.CountAsync(r => r.PartId == part));
        Assert.Equal(1, active);
    }

    [Fact]
    public async Task Reservas_e_vendas_de_balcao_simultaneas_nunca_vendem_mais_que_o_saldo()
    {
        const int onHand = 5;
        var part = await fx.CreatePartAsync(fx.TenantA, onHand);

        var attempts = Enumerable.Range(0, 40).Select(i => Task.Run(async () =>
            i % 2 == 0 ? (await ReserveAsync(part)).Reserved : (await SellAsync(part)).Sold));
        var results = await Task.WhenAll(attempts);

        Assert.Equal(onHand, results.Count(ok => ok));
        var stock = await fx.GetStockAsync(fx.TenantA, part);
        Assert.Equal(0, stock.Available);
        Assert.True(stock.OnHand >= 0 && stock.Reserved >= 0);
    }

    [Fact]
    public async Task Confirmacao_converte_reserva_em_baixa_e_registra_movimento()
    {
        var part = await fx.CreatePartAsync(fx.TenantA, onHand: 3);
        var reservation = (await ReserveAsync(part, quantity: 2)).ReservationId!.Value;

        Assert.Equal(ConfirmReservationOutcome.Confirmed, await ConfirmAsync(reservation));

        var stock = await fx.GetStockAsync(fx.TenantA, part);
        Assert.Equal((1, 0, 1), (stock.OnHand, stock.Reserved, stock.Available));
        var movement = await fx.QueryAsync(fx.TenantA, db => db.StockMovements.SingleAsync(m => m.ReservationId == reservation));
        Assert.Equal((-2, StockMovementReason.ReservationConverted), (movement.Delta, movement.Reason));
    }

    [Fact]
    public async Task Confirmacao_repetida_nao_baixa_duas_vezes()
    {
        var part = await fx.CreatePartAsync(fx.TenantA, onHand: 3);
        var reservation = (await ReserveAsync(part)).ReservationId!.Value;
        await ConfirmAsync(reservation);

        Assert.Equal(ConfirmReservationOutcome.AlreadyConfirmed, await ConfirmAsync(reservation));
        Assert.Equal(2, (await fx.GetStockAsync(fx.TenantA, part)).OnHand);
    }

    [Fact]
    public async Task Cancelamento_devolve_o_saldo_e_repetir_nao_tem_efeito()
    {
        var part = await fx.CreatePartAsync(fx.TenantA, onHand: 1);
        var reservation = (await ReserveAsync(part)).ReservationId!.Value;

        await fx.InvokeAsync(fx.TenantA, new ReleaseReservation(reservation));
        await fx.InvokeAsync(fx.TenantA, new ReleaseReservation(reservation));

        var stock = await fx.GetStockAsync(fx.TenantA, part);
        Assert.Equal((1, 0, 1), (stock.OnHand, stock.Reserved, stock.Available));
        Assert.Equal(ReservationStatus.Released, (await fx.GetReservationAsync(fx.TenantA, reservation)).Status);
    }

    [Fact]
    public async Task Expiracao_so_libera_depois_do_prazo()
    {
        var part = await fx.CreatePartAsync(fx.TenantA, onHand: 1);
        var reservation = (await ReserveAsync(part)).ReservationId!.Value;

        await fx.InvokeAsync(fx.TenantA, new ExpireReservation(reservation)); // antes do prazo: nada muda
        Assert.Equal(ReservationStatus.Active, (await fx.GetReservationAsync(fx.TenantA, reservation)).Status);

        await fx.ExecuteAsync(fx.TenantA, $"UPDATE stock_reservations SET expires_at = now() - interval '1 minute' WHERE id = {reservation}");
        await fx.InvokeAsync(fx.TenantA, new ExpireReservation(reservation));

        Assert.Equal(ReservationStatus.Expired, (await fx.GetReservationAsync(fx.TenantA, reservation)).Status);
        Assert.Equal(1, (await fx.GetStockAsync(fx.TenantA, part)).Available);
    }

    [Fact]
    public async Task Pagamento_apos_expiracao_baixa_se_ainda_houver_saldo()
    {
        var part = await fx.CreatePartAsync(fx.TenantA, onHand: 1);
        var reservation = (await ReserveAsync(part)).ReservationId!.Value;
        await ExpireNowAsync(reservation);

        Assert.Equal(ConfirmReservationOutcome.ConfirmedAfterExpiry, await ConfirmAsync(reservation));
        Assert.Equal(0, (await fx.GetStockAsync(fx.TenantA, part)).OnHand);
    }

    [Fact]
    public async Task Pagamento_apos_expiracao_sem_saldo_pede_estorno()
    {
        var part = await fx.CreatePartAsync(fx.TenantA, onHand: 1);
        var reservation = (await ReserveAsync(part)).ReservationId!.Value;
        await ExpireNowAsync(reservation);
        Assert.True((await SellAsync(part)).Sold); // vendida no balcão enquanto isso

        Assert.Equal(ConfirmReservationOutcome.OutOfStock, await ConfirmAsync(reservation));
        var stock = await fx.GetStockAsync(fx.TenantA, part);
        Assert.Equal((0, 0), (stock.OnHand, stock.Reserved));
    }

    [Fact]
    public async Task Reserva_agenda_mensagem_de_expiracao_duravel()
    {
        var part = await fx.CreatePartAsync(fx.TenantA, onHand: 1);
        var reservation = (await ReserveAsync(part)).ReservationId!.Value;

        await using var connection = new NpgsqlConnection(fx.TenantsConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            "SELECT count(*) FROM wolverine.wolverine_incoming_envelopes WHERE status = 'Scheduled' AND message_type LIKE '%ExpireReservation%'",
            connection);
        var scheduled = (long)(await command.ExecuteScalarAsync())!;

        Assert.True(scheduled >= 1, $"Nenhuma expiração agendada para a reserva {reservation}.");
    }

    [Fact]
    public async Task Tenant_B_nao_reserva_peca_do_tenant_A()
    {
        var part = await fx.CreatePartAsync(fx.TenantA, onHand: 1);

        var result = await ReserveAsync(part, tenant: fx.TenantB);

        Assert.False(result.Reserved);
        Assert.Equal(1, (await fx.GetStockAsync(fx.TenantA, part)).Available);
    }

    [Fact]
    public async Task Banco_recusa_saldo_negativo_mesmo_com_SQL_direto()
    {
        var part = await fx.CreatePartAsync(fx.TenantA, onHand: 1);

        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            fx.ExecuteAsync(fx.TenantA, $"UPDATE stocks SET reserved = on_hand + 1 WHERE part_id = {part}"));

        Assert.Equal(PostgresErrorCodes.CheckViolation, ex.SqlState);
    }

    /// <summary>Leva a reserva para depois do prazo e dispara a expiração real (o handler).</summary>
    private async Task ExpireNowAsync(Guid reservation)
    {
        await fx.ExecuteAsync(fx.TenantA, $"UPDATE stock_reservations SET expires_at = now() - interval '1 minute' WHERE id = {reservation}");
        await fx.InvokeAsync(fx.TenantA, new ExpireReservation(reservation));
    }
}
