using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Ecommerce.Application.Fiscal;
using Ecommerce.Application.Integrations;
using Ecommerce.Application.Tenancy;
using Ecommerce.Domain.Fiscal;
using Ecommerce.Infrastructure.Integrations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Ecommerce.Infrastructure.Fiscal;

public sealed class FiscalOptions
{
    public const string Section = "Fiscal";

    /// <summary>Só dev e testes: lojas no modo "pelo provedor" emitem no <see cref="FakeFiscalProvider"/>. Nunca em produção.</summary>
    public bool UseFakeProvider { get; set; }
}

/// <summary>
/// Provedor fiscal falso (dev e testes), sem estado: tudo sai da referência, então API e Worker respondem igual.
/// Envio: NCM "00000000" é rejeitado na hora (como erro de validação); o resto fica "processando". Consulta: autorizada,
/// com uma chave de acesso válida (dígito verificador correto) derivada da referência. Nada tem valor fiscal.
/// </summary>
public sealed class FakeFiscalProvider : IFiscalProvider
{
    public const string ProviderName = "nfe-simulada";
    public const string RejectedNcm = "00000000";
    /// <summary>CNPJ de exemplo usado nas chaves simuladas.</summary>
    private const string SampleCnpj = "11222333000181";

    public string Name => ProviderName;

    public Task<FiscalProviderResult> EmitAsync(FiscalInvoice invoice, CancellationToken ct = default) =>
        Task.FromResult(invoice.Items.Any(i => i.Ncm == RejectedNcm)
            ? new FiscalProviderResult(FiscalProviderStatus.Rejected, Message: "Rejeição 778: Informado NCM inexistente (simulação).")
            : new FiscalProviderResult(FiscalProviderStatus.Processing));

    public Task<FiscalProviderResult> QueryAsync(string reference, CancellationToken ct = default)
    {
        var (key, number) = KeyFor(reference);
        return Task.FromResult(new FiscalProviderResult(FiscalProviderStatus.Authorized, key, number, 1));
    }

    public Task<FiscalProviderResult> CancelAsync(string reference, string justification, CancellationToken ct = default) =>
        Task.FromResult(new FiscalProviderResult(FiscalProviderStatus.Canceled, KeyFor(reference).Key));

    public Task<byte[]?> DownloadAsync(string reference, FiscalFile file, CancellationToken ct = default)
    {
        var key = KeyFor(reference).Key;
        var content = file == FiscalFile.Xml
            ? $"""<?xml version="1.0" encoding="UTF-8"?><nfeProc versao="4.00"><!-- simulação: sem valor fiscal --><protNFe><infProt><chNFe>{key}</chNFe></infProt></protNFe></nfeProc>"""
            : MinimalPdf($"DANFE simulado - chave {key} - sem valor fiscal");
        return Task.FromResult<byte[]?>(Encoding.UTF8.GetBytes(content));
    }

    /// <summary>Chave modelo 55, série 1, número e código numérico tirados do hash da referência.</summary>
    public static (string Key, int Number) KeyFor(string reference)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(reference));
        var number = (int)(BitConverter.ToUInt32(hash, 0) % 999_999_999) + 1;
        var code = (BitConverter.ToUInt32(hash, 4) % 100_000_000).ToString("D8", CultureInfo.InvariantCulture);
        var body = string.Create(CultureInfo.InvariantCulture, $"352610{SampleCnpj}55001{number:D9}1{code}");
        return (body + AccessKey.CheckDigit(body).ToString(CultureInfo.InvariantCulture), number);
    }

    private static string MinimalPdf(string text)
    {
        var stream = $"BT /F1 12 Tf 40 800 Td ({text}) Tj ET";
        return "%PDF-1.4\n1 0 obj<</Type/Catalog/Pages 2 0 R>>endobj 2 0 obj<</Type/Pages/Kids[3 0 R]/Count 1>>endobj " +
            "3 0 obj<</Type/Page/Parent 2 0 R/MediaBox[0 0 595 842]/Contents 4 0 R/Resources<</Font<</F1 5 0 R>>>>>>endobj " +
            string.Create(CultureInfo.InvariantCulture, $"4 0 obj<</Length {stream.Length}>>stream\n{stream}\nendstream endobj ") +
            "5 0 obj<</Type/Font/Subtype/Type1/BaseFont/Helvetica>>endobj\ntrailer<</Root 1 0 R>>\n%%EOF";
    }
}

/// <summary>Provedor fiscal com cada chamada no log de integração (RF29), ligado ao pedido da nota.</summary>
public sealed class LoggedFiscalProvider(IFiscalProvider inner, IIntegrationLog log, Guid orderId) : IFiscalProvider
{
    public string Name => inner.Name;

    public Task<FiscalProviderResult> EmitAsync(FiscalInvoice invoice, CancellationToken ct = default) =>
        Logged.RunAsync(log, Name, "emitir NF-e", () => inner.EmitAsync(invoice, ct), _ => invoice.Reference, orderId, null, ct, Failed);

    public Task<FiscalProviderResult> QueryAsync(string reference, CancellationToken ct = default) =>
        Logged.RunAsync(log, Name, "consultar NF-e", () => inner.QueryAsync(reference, ct), r => r.AccessKey ?? reference, orderId, null, ct, Failed);

    public Task<FiscalProviderResult> CancelAsync(string reference, string justification, CancellationToken ct = default) =>
        Logged.RunAsync(log, Name, "cancelar NF-e", () => inner.CancelAsync(reference, justification, ct), r => r.AccessKey ?? reference, orderId, null, ct,
            r => r.Status == FiscalProviderStatus.Canceled ? null : r.Message ?? "Cancelamento não aceito.");

    public Task<byte[]?> DownloadAsync(string reference, FiscalFile file, CancellationToken ct = default) =>
        Logged.RunAsync(log, Name, file == FiscalFile.Xml ? "baixar XML da NF-e" : "baixar DANFE", () => inner.DownloadAsync(reference, file, ct),
            _ => reference, orderId, null, ct, b => b is null ? "Arquivo ainda não disponível." : null);

    private static string? Failed(FiscalProviderResult result) => result.Status == FiscalProviderStatus.Rejected ? result.Message ?? "Rejeitada." : null;
}

/// <summary>
/// Provedor da loja do escopo: só com o modo "pelo provedor". Até o adaptador da Focus NFe (RF19 parte B), só o falso
/// em dev e testes; em produção, nenhum (a tela orienta a usar a emissão externa).
/// </summary>
public sealed class FiscalProviderResolver(Persistence.TenantDbContext db, IOptions<FiscalOptions> options) : IFiscalProviderResolver
{
    public async Task<IFiscalProvider?> ResolveAsync(CancellationToken ct = default)
    {
        var settings = await db.StoreSettings.AsNoTracking().Select(s => s.Fiscal).SingleOrDefaultAsync(ct);
        if (settings is not { Mode: FiscalMode.Provider }) return null;
        return options.Value.UseFakeProvider ? new FakeFiscalProvider() : null;
    }
}
