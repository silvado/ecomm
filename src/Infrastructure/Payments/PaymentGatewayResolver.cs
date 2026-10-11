using Ecommerce.Application.Payments;
using Ecommerce.Application.Tenancy;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Payments;

/// <summary>
/// Gateway da loja do escopo. Hoje só o fake (dev/testes); o Mercado Pago, com as credenciais da loja no cofre,
/// entra com o adaptador. Sem nenhum, a loja não recebe pagamento online.
/// </summary>
public sealed class PaymentGatewayResolver(IOptions<PaymentOptions> options, ITenantContext tenant) : IPaymentGatewayResolver
{
    public Task<IPaymentGateway?> ResolveAsync(CancellationToken ct = default) =>
        Task.FromResult<IPaymentGateway?>(Fake());

    public Task<IPaymentGateway?> ResolveAsync(string name, CancellationToken ct = default) =>
        Task.FromResult<IPaymentGateway?>(name == FakePaymentGateway.GatewayName ? Fake() : null);

    private FakePaymentGateway? Fake() =>
        options.Value.UseFakeGateway && tenant.TenantId is { } tenantId ? new FakePaymentGateway(tenantId) : null;
}
