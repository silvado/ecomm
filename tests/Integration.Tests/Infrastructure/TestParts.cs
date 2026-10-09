using Ecommerce.Domain.Catalog;

namespace Ecommerce.Integration.Tests.Infrastructure;

/// <summary>Peça mínima válida para testes que só precisam de uma peça existir.</summary>
public static class TestParts
{
    public static Part New(Guid tenantId, string internalCode, string title, decimal price = 100m) =>
        Part.Create(tenantId, internalCode, new PartDetails(title, null, PartCondition.Used, price, null, null, null, null, null), DateTimeOffset.UtcNow);
}