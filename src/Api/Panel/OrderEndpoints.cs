using System.Globalization;
using Ecommerce.Api.Auth;
using Ecommerce.Application.Orders;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Orders;
using Microsoft.AspNetCore.Http.HttpResults;
using Wolverine;

namespace Ecommerce.Api.Panel;

public sealed record CancelOrderRequest(string? Motivo);

public sealed record ShipOrderRequest(string? Rastreio);

/// <summary>Pedidos de todos os canais no painel (RF17): lista com filtros, detalhe e ações da loja. Dono e Operador.</summary>
public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this RouteGroupBuilder panel)
    {
        var orders = panel.MapGroup("/pedidos").RequirePermission(Permission.OrdersManage);

        // Valores no formato do JSON (pendingPayment, paid, site...), sem diferenciar maiúsculas; "situacao" aceita vários
        // separados por vírgula; "de"/"ate" em AAAA-MM-DD (dias da loja); "busca": número, nome, e-mail ou CPF.
        orders.MapGet("/", async Task<Results<Ok<OrderPage>, ProblemHttpResult>> (
            string? situacao, string? origem, string? de, string? ate, string? busca, int? pagina, int? tamanho,
            IOrderQueries queries, CancellationToken ct) =>
        {
            var statuses = new List<OrderStatus>();
            foreach (var value in (situacao ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!TryParse<OrderStatus>(value, out var status))
                    return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, $"Situação inválida: {value}.");
                statuses.Add(status);
            }
            OrderOrigin? origin = null;
            if (!string.IsNullOrWhiteSpace(origem))
            {
                if (!TryParse<OrderOrigin>(origem, out var parsed)) return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, "Origem inválida.");
                origin = parsed;
            }
            if (!TryDate(de, out var from) || !TryDate(ate, out var to))
                return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, "Data inválida: use AAAA-MM-DD.");
            if (from > to) return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, "A data inicial é depois da final.");

            return TypedResults.Ok(await queries.SearchAsync(new OrderSearch(statuses, origin, from, to, busca, pagina ?? 1, tamanho ?? 25), ct));
        });

        orders.MapGet("/{id:guid}", async Task<Results<Ok<OrderView>, ProblemHttpResult>> (Guid id, IOrderQueries queries, CancellationToken ct) =>
            await queries.GetAsync(id, ct) is { } order
                ? TypedResults.Ok(order)
                : AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Pedido não encontrado."));

        orders.MapPost("/{id:guid}/aprovar", (Guid id, HttpContext http, IMessageBus bus, IOrderQueries queries, CancellationToken ct) =>
            ChangeAsync(new ChangeOrder(id, OrderAction.Approve, http.GetPanelUser().UserId), http, bus, queries, ct));

        orders.MapPost("/{id:guid}/cancelar", (Guid id, CancelOrderRequest request, HttpContext http, IMessageBus bus, IOrderQueries queries, CancellationToken ct) =>
            ChangeAsync(new ChangeOrder(id, OrderAction.Cancel, http.GetPanelUser().UserId, Note: request.Motivo), http, bus, queries, ct));

        orders.MapPost("/{id:guid}/enviar", (Guid id, ShipOrderRequest request, HttpContext http, IMessageBus bus, IOrderQueries queries, CancellationToken ct) =>
            ChangeAsync(new ChangeOrder(id, OrderAction.Ship, http.GetPanelUser().UserId, TrackingCode: request.Rastreio), http, bus, queries, ct));

        orders.MapPost("/{id:guid}/entregar", (Guid id, HttpContext http, IMessageBus bus, IOrderQueries queries, CancellationToken ct) =>
            ChangeAsync(new ChangeOrder(id, OrderAction.Deliver, http.GetPanelUser().UserId), http, bus, queries, ct));

        orders.MapPost("/{id:guid}/retirar", (Guid id, HttpContext http, IMessageBus bus, IOrderQueries queries, CancellationToken ct) =>
            ChangeAsync(new ChangeOrder(id, OrderAction.PickUp, http.GetPanelUser().UserId), http, bus, queries, ct));
    }

    /// <summary>Executa a ação pela fila do tenant (transação, estoque e histórico juntos) e devolve o pedido atualizado.</summary>
    private static async Task<Results<Ok<OrderView>, ProblemHttpResult>> ChangeAsync(
        ChangeOrder command, HttpContext http, IMessageBus bus, IOrderQueries queries, CancellationToken ct)
    {
        var tenantId = http.GetPanelUser().Access.TenantId.ToString();
        var result = await bus.InvokeForTenantAsync<ChangeOrderResult>(tenantId, command, ct);
        return result.Outcome switch
        {
            ChangeOrderOutcome.Done => TypedResults.Ok((await queries.GetAsync(command.OrderId, ct))!),
            ChangeOrderOutcome.NotFound => AuthEndpoints.Problem(StatusCodes.Status404NotFound, result.Message!),
            ChangeOrderOutcome.Invalid => AuthEndpoints.Problem(StatusCodes.Status400BadRequest, result.Message!),
            _ => AuthEndpoints.Problem(StatusCodes.Status409Conflict, result.Message!),
        };
    }

    private static bool TryParse<T>(string value, out T parsed) where T : struct, Enum =>
        Enum.TryParse(value, ignoreCase: true, out parsed) && Enum.IsDefined(parsed) && !value.All(char.IsAsciiDigit);

    private static bool TryDate(string? value, out DateOnly? date)
    {
        date = null;
        if (string.IsNullOrWhiteSpace(value)) return true;
        if (!DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)) return false;
        date = parsed;
        return true;
    }
}
