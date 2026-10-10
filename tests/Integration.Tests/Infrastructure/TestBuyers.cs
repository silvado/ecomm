using System.Security.Cryptography;
using Ecommerce.Domain.Orders;

namespace Ecommerce.Integration.Tests.Infrastructure;

/// <summary>Compradores de teste com CPF válido sorteado (nenhum CPF real no repositório).</summary>
public static class TestBuyers
{
    public static string Cpf()
    {
        while (true)
        {
            var body = string.Concat(Enumerable.Range(0, 9).Select(_ => RandomNumberGenerator.GetInt32(10)));
            var first = CheckDigit(body);
            var full = body + first + CheckDigit(body + first);
            if (Ecommerce.Domain.Orders.Cpf.TryParse(full, out _)) return full;
        }
    }

    public static BuyerDetails Buyer() => new("Maria da Silva", "maria@exemplo.com.br", "(48) 99999-1234", Cpf());

    public static DeliveryAddress Address(string cep = "88015-200") =>
        new(cep, "Rua Felipe Schmidt", "100", "sala 2", "Centro", "Florianópolis", "sc");

    private static int CheckDigit(string body)
    {
        var sum = 0;
        for (var i = 0; i < body.Length; i++) sum += (body[i] - '0') * (body.Length + 1 - i);
        var remainder = sum % 11;
        return remainder < 2 ? 0 : 11 - remainder;
    }
}
