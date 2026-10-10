using Ecommerce.Api.Auth;
using Ecommerce.Application.Orders;
using Ecommerce.Domain.Identity;
using Ecommerce.Domain.Orders;
using Microsoft.AspNetCore.Http.HttpResults;

namespace Ecommerce.Api.Panel;

/// <summary>Pedidos da loja no painel (RF14; o painel unificado com filtros é o RF17): Dono e Operador.</summary>
public static class OrderEndpoints
{
    public static void MapOrderEndpoints(this RouteGroupBuilder panel)
    {
        var orders = panel.MapGroup("/pedidos").RequirePermission(Permission.OrdersManage);

        // "situacao" no mesmo formato do JSON (pendingPayment, paid, canceled), sem diferenciar maiúsculas.
        orders.MapGet("/", async Task<Results<Ok<OrderPage>, ProblemHttpResult>> (
            string? situacao, int? pagina, int? tamanho, IOrderQueries queries, CancellationToken ct) =>
        {
            OrderStatus? status = null;
            if (!string.IsNullOrWhiteSpace(situacao))
            {
                if (!Enum.TryParse<OrderStatus>(situacao, ignoreCase: true, out var parsed) || !Enum.IsDefined(parsed))
                    return AuthEndpoints.Problem(StatusCodes.Status400BadRequest, "Situação inválida: use pendingPayment, paid ou canceled.");
                status = parsed;
            }
            return TypedResults.Ok(await queries.SearchAsync(new OrderSearch(status, pagina ?? 1, tamanho ?? 25), ct));
        });

        orders.MapGet("/{id:guid}", async Task<Results<Ok<OrderView>, ProblemHttpResult>> (Guid id, IOrderQueries queries, CancellationToken ct) =>
            await queries.GetAsync(id, ct) is { } order
                ? TypedResults.Ok(order)
                : AuthEndpoints.Problem(StatusCodes.Status404NotFound, "Pedido não encontrado."));
    }
}
