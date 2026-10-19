using Ecommerce.Domain.Catalog;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Catalog;

/// <summary>
/// Código interno gerado (RF08) quando a loja deixa o campo em branco: P-000001, P-000002… por loja. Roda na transação do
/// cadastro: a linha do contador fica travada até o fim (cadastros simultâneos recebem números diferentes) e um cadastro
/// desfeito devolve o número. Código já usado à mão (ex.: alguém digitou P-000010) é pulado.
/// </summary>
public static class PartCodes
{
    /// <param name="taken">Códigos ainda não gravados que também contam como usados (ex.: os da mesma planilha).</param>
    public static async Task<string> NextAsync(TenantDbContext db, IReadOnlySet<string>? taken, CancellationToken ct)
    {
        var tenantId = db.RequireTenantId();
        while (true)
        {
            var number = (await db.Database.SqlQuery<long>($"""
                INSERT INTO part_code_counters (tenant_id, last_number) VALUES ({tenantId}, 1)
                ON CONFLICT (tenant_id) DO UPDATE SET last_number = part_code_counters.last_number + 1
                RETURNING last_number AS "Value"
                """).ToListAsync(ct)).Single();
            var code = Part.GeneratedCode(number);
            if (taken?.Contains(code) != true && !await db.Parts.AnyAsync(p => p.InternalCode == code, ct)) return code;
        }
    }
}
