using System.Globalization;
using System.IO.Compression;
using System.Text;
using Ecommerce.Application.Storage;
using Ecommerce.Domain.Catalog;
using Ecommerce.Domain.Orders;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Privacy;

/// <summary>
/// Monta o ZIP da exportação (RF39 CA1) num arquivo temporário: CSVs (separador ";", decimais com vírgula, datas no
/// horário de Brasília — abrem direto no Excel em português), fotos originais e XML/DANFE das notas. Segredos do cofre
/// (tenant_secrets, chaves de dados) nunca entram (CA3).
/// </summary>
public sealed class DataExportBuilder(TenantDbContext db, IFileStorage storage)
{
    private static readonly TimeZoneInfo StoreTimeZone = TimeZoneInfo.FindSystemTimeZoneById("America/Sao_Paulo");
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly int[] SingleRow = [0];

    /// <summary>Escreve o pacote em <paramref name="output"/> (fica aberto) e devolve quantos arquivos entraram.</summary>
    public async Task<int> WriteAsync(Stream output, string storeName, DateTimeOffset now, CancellationToken ct)
    {
        var entries = 0;
        using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);

        var parts = await db.Parts.AsNoTracking().OrderBy(p => p.InternalCode).ToListAsync(ct);
        var stocks = await db.Stocks.AsNoTracking().ToDictionaryAsync(s => s.PartId, ct);
        var codes = parts.ToDictionary(p => p.Id, p => p.InternalCode);
        await CsvAsync(zip, "pecas.csv", parts, ct,
            ("id", p => p.Id), ("codigo_interno", p => p.InternalCode), ("titulo", p => p.Title), ("descricao", p => p.Description),
            ("condicao", p => Condition(p.Condition)), ("preco", p => p.Price), ("situacao", p => PartStatusLabel(p.Status)),
            ("estoque_fisico", p => stocks.GetValueOrDefault(p.Id)?.OnHand), ("estoque_reservado", p => stocks.GetValueOrDefault(p.Id)?.Reserved),
            ("comprimento_cm", p => p.LengthCm), ("largura_cm", p => p.WidthCm), ("altura_cm", p => p.HeightCm), ("peso_g", p => p.WeightG),
            ("veiculo_doador_id", p => p.DonorVehicleId), ("etiqueta_rastreabilidade", p => p.TraceabilityTag),
            ("campos_extras", p => p.CustomFields.Count == 0 ? null : string.Join(" | ", p.CustomFields.Select(f => $"{f.Key}: {f.Value}"))),
            ("ncm", p => p.Ncm), ("criada_em", p => p.CreatedAt), ("atualizada_em", p => p.UpdatedAt));
        entries++;

        await CsvAsync(zip, "pecas_codigos_oem.csv", await db.PartOemCodes.AsNoTracking().ToListAsync(ct), ct,
            ("peca", c => codes.GetValueOrDefault(c.PartId)), ("codigo_oem", c => c.Code));
        entries++;

        var compat = await (from c in db.PartCompatibilities.AsNoTracking()
                            join v in db.VehicleVersions.AsNoTracking() on c.VehicleVersionId equals v.Id
                            join m in db.VehicleModels.AsNoTracking() on v.ModelId equals m.Id
                            join b in db.VehicleBrands.AsNoTracking() on m.BrandId equals b.Id
                            select new { c.PartId, Brand = b.Name, Model = m.Name, Version = v.Engine, c.YearFrom, c.YearTo }).ToListAsync(ct);
        await CsvAsync(zip, "pecas_compatibilidade.csv", compat, ct,
            ("peca", c => codes.GetValueOrDefault(c.PartId)), ("marca", c => c.Brand), ("modelo", c => c.Model), ("versao", c => c.Version),
            ("ano_de", c => c.YearFrom), ("ano_ate", c => c.YearTo));
        entries++;

        await CsvAsync(zip, "veiculos_doadores.csv", await db.DonorVehicles.AsNoTracking().OrderBy(d => d.CreatedAt).ToListAsync(ct), ct,
            ("id", d => d.Id), ("placa", d => d.Plate), ("chassi", d => d.Chassis), ("renavam", d => d.Renavam), ("marca", d => d.Brand),
            ("modelo", d => d.Model), ("ano_modelo", d => d.ModelYear), ("cor", d => d.Color), ("certidao_baixa", d => d.DeregistrationCertificate),
            ("chave_nfe_entrada", d => d.EntryInvoiceKey), ("data_entrada", d => d.EntryDate?.ToString("dd/MM/yyyy", PtBr)), ("observacoes", d => d.Notes),
            ("criado_em", d => d.CreatedAt));
        entries++;

        await CsvAsync(zip, "movimentos_estoque.csv", await db.StockMovements.AsNoTracking().OrderBy(m => m.At).ToListAsync(ct), ct,
            ("quando", m => m.At), ("peca", m => codes.GetValueOrDefault(m.PartId)), ("quantidade", m => m.Delta), ("motivo", m => m.Reason.ToString()),
            ("referencia", m => m.Reference));
        entries++;

        var orders = await db.CustomerOrders.AsNoTracking().OrderBy(o => o.Number).ToListAsync(ct);
        var numbers = orders.ToDictionary(o => o.Id, o => o.Number);
        await CsvAsync(zip, "pedidos.csv", orders, ct,
            ("numero", o => o.Number), ("origem", o => Origin(o.Origin)), ("situacao", o => OrderStatusLabel(o.Status)), ("feito_em", o => o.PlacedAt),
            ("comprador", o => o.BuyerName), ("email", o => o.BuyerEmail), ("telefone", o => o.BuyerPhone), ("cpf", o => o.BuyerCpf), ("cnpj", o => o.BuyerCnpj),
            ("entrega", o => o.DeliveryMethod == DeliveryMethod.Pickup ? "Retirada" : "Entrega"), ("cep", o => o.DeliveryPostalCode),
            ("endereco", o => o.DeliveryStreet), ("numero_endereco", o => o.DeliveryNumber), ("complemento", o => o.DeliveryComplement),
            ("bairro", o => o.DeliveryDistrict), ("cidade", o => o.DeliveryCity), ("uf", o => o.DeliveryState),
            ("transportadora", o => o.ShippingCarrier), ("servico_frete", o => o.ShippingService), ("rastreio", o => o.TrackingCode),
            ("forma_pagamento_balcao", o => o.PaymentMethod?.ToString()), ("total_pecas", o => o.ItemsTotal), ("frete", o => o.ShippingTotal), ("total", o => o.Total),
            ("pago_em", o => o.PaidAt), ("enviado_em", o => o.ShippedAt), ("entregue_em", o => o.DeliveredAt), ("cancelado_em", o => o.CanceledAt),
            ("motivo_cancelamento", o => o.CancelReason), ("anonimizado_em", o => o.AnonymizedAt));
        entries++;

        await CsvAsync(zip, "pedidos_itens.csv", await db.OrderItems.AsNoTracking().ToListAsync(ct), ct,
            ("pedido", i => numbers.GetValueOrDefault(i.OrderId)), ("peca", i => i.InternalCode), ("titulo", i => i.Title),
            ("quantidade", i => i.Quantity), ("preco_unitario", i => i.UnitPrice));
        entries++;

        await CsvAsync(zip, "pedidos_historico.csv", await db.OrderHistory.AsNoTracking().OrderBy(h => h.At).ToListAsync(ct), ct,
            ("pedido", h => numbers.GetValueOrDefault(h.OrderId)), ("quando", h => h.At), ("de", h => h.FromStatus is { } f ? OrderStatusLabel(f) : null),
            ("para", h => OrderStatusLabel(h.ToStatus)), ("quem", h => h.Actor.ToString()), ("observacao", h => h.Note));
        entries++;

        await CsvAsync(zip, "devolucoes.csv", await db.OrderReturns.AsNoTracking().OrderBy(r => r.At).ToListAsync(ct), ct,
            ("pedido", r => numbers.GetValueOrDefault(r.OrderId)), ("quando", r => r.At), ("motivo", r => r.Reason.ToString()),
            ("pecas", r => string.Join(" | ", r.Lines.Select(l => $"{codes.GetValueOrDefault(l.PartId)} x{l.Quantity}"))),
            ("reembolso", r => r.RefundAmount), ("forma_reembolso", r => r.RefundMethod?.ToString()), ("observacao", r => r.Note));
        entries++;

        // Clientes: os compradores dos pedidos (a loja não tem cadastro à parte), um por e-mail ou documento.
        var customers = orders.Where(o => o.AnonymizedAt is null)
            .GroupBy(o => o.BuyerEmail.Length > 0 ? o.BuyerEmail : $"doc:{o.BuyerCnpj ?? o.BuyerCpf}")
            .Where(g => g.Key != "doc:")
            .Select(g => (Last: g.MaxBy(o => o.PlacedAt)!, Orders: g.Count()))
            .Select(c => new { c.Last.BuyerName, c.Last.BuyerEmail, c.Last.BuyerPhone, c.Last.BuyerCpf, c.Last.BuyerCnpj, c.Orders, Last = c.Last.PlacedAt })
            .OrderBy(c => c.BuyerName, StringComparer.Create(PtBr, ignoreCase: true)).ToList();
        await CsvAsync(zip, "clientes.csv", customers, ct,
            ("nome", c => c.BuyerName), ("email", c => c.BuyerEmail), ("telefone", c => c.BuyerPhone), ("cpf", c => c.BuyerCpf), ("cnpj", c => c.BuyerCnpj),
            ("pedidos", c => c.Orders), ("ultimo_pedido", c => c.Last));
        entries++;

        var invoices = await db.FiscalDocuments.AsNoTracking().OrderBy(f => f.RequestedAt).ToListAsync(ct);
        await CsvAsync(zip, "notas_fiscais.csv", invoices, ct,
            ("pedido", f => numbers.GetValueOrDefault(f.OrderId)), ("origem", f => f.Source.ToString()), ("situacao", f => f.Status.ToString()),
            ("chave", f => f.AccessKey), ("numero", f => f.Number), ("serie", f => f.Series), ("destinatario", f => f.Recipient?.Name),
            ("documento_destinatario", f => f.Recipient?.Document), ("autorizada_em", f => f.AuthorizedAt), ("cancelada_em", f => f.CanceledAt),
            ("arquivo_xml", f => f.XmlStorageKey is null ? null : $"notas/{FileBase(f)}.xml"), ("arquivo_danfe", f => f.DanfeStorageKey is null ? null : $"notas/{FileBase(f)}.pdf"));
        entries++;
        foreach (var invoice in invoices)
        {
            if (invoice.XmlStorageKey is { } xml && await CopyAsync(zip, StorageArea.Private, xml, $"notas/{FileBase(invoice)}.xml", ct)) entries++;
            if (invoice.DanfeStorageKey is { } danfe && await CopyAsync(zip, StorageArea.Private, danfe, $"notas/{FileBase(invoice)}.pdf", ct)) entries++;
        }

        var listings = await (from l in db.ChannelListings.AsNoTracking()
                              join c in db.ChannelConnections.AsNoTracking() on l.ConnectionId equals c.Id
                              select new { l.PartId, c.Channel, l.ExternalId, l.Status, l.LastSyncedAt }).ToListAsync(ct);
        await CsvAsync(zip, "anuncios.csv", listings, ct,
            ("peca", l => codes.GetValueOrDefault(l.PartId)), ("canal", l => l.Channel), ("id_no_canal", l => l.ExternalId),
            ("situacao", l => l.Status.ToString()), ("sincronizado_em", l => l.LastSyncedAt));
        entries++;

        var branding = await db.StoreBrandings.AsNoTracking().SingleOrDefaultAsync(ct);
        var settings = await db.StoreSettings.AsNoTracking().SingleOrDefaultAsync(ct);
        await CsvAsync(zip, "loja.csv", SingleRow, ct,
            ("nome", _ => storeName), ("cor_principal", _ => branding?.PrimaryColor), ("cor_fundo", _ => branding?.BackgroundColor), ("cor_texto", _ => branding?.TextColor),
            ("sobre", _ => branding?.About), ("trocas_e_devolucoes", _ => branding?.ReturnPolicy), ("rodape", _ => branding?.Footer),
            ("politica_de_privacidade", _ => branding?.PrivacyPolicy), ("termos_de_uso", _ => branding?.Terms),
            ("cep_origem", _ => settings?.OriginPostalCode), ("retirada", _ => settings?.PickupEnabled), ("endereco_retirada", _ => settings?.PickupAddress));
        entries++;

        // Fotos originais (a melhor qualidade guardada), por peça e na ordem da vitrine.
        var photos = await db.PartPhotos.AsNoTracking().OrderBy(p => p.PartId).ThenBy(p => p.Position).ToListAsync(ct);
        foreach (var photo in photos)
        {
            var name = $"fotos/{Safe(codes.GetValueOrDefault(photo.PartId) ?? photo.PartId.ToString())}/{photo.Position + 1:D2}-{photo.Id}{Extension(photo.OriginalContentType)}";
            if (await CopyAsync(zip, StorageArea.Private, PartPhoto.OriginalKey(photo.TenantId, photo.PartId, photo.Id), name, ct)) entries++;
        }

        await TextAsync(zip, "LEIAME.txt", $"""
            Exportação completa — {storeName}
            Gerada em {Local(now):dd/MM/yyyy HH:mm} (horário de Brasília).

            Planilhas (CSV, separadas por ponto e vírgula, abrem no Excel ou no LibreOffice):
            - pecas.csv, pecas_codigos_oem.csv, pecas_compatibilidade.csv, veiculos_doadores.csv, movimentos_estoque.csv
            - pedidos.csv, pedidos_itens.csv, pedidos_historico.csv, devolucoes.csv, clientes.csv
            - notas_fiscais.csv (arquivos XML e DANFE na pasta notas)
            - anuncios.csv, loja.csv
            Fotos originais das peças: pasta fotos, uma subpasta por código interno.

            Contém dados pessoais de compradores: guarde em local seguro e apague quando não precisar mais (LGPD).
            Senhas, chaves de pagamento e o certificado digital não são exportados.
            """, ct);
        return entries + 1;
    }

    private static string FileBase(Domain.Fiscal.FiscalDocument f) => f.AccessKey ?? f.Id.ToString();

    private static string Extension(string contentType) => contentType switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/webp" => ".webp",
        _ => string.Empty,
    };

    private static string Safe(string value) => new(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());

    private async Task<bool> CopyAsync(ZipArchive zip, StorageArea area, string key, string name, CancellationToken ct)
    {
        var file = await storage.GetAsync(area, key, ct);
        if (file is null) return false;
        await using var content = file.Content;
        var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
        await using var target = await entry.OpenAsync(ct);
        await content.CopyToAsync(target, ct);
        return true;
    }

    private static async Task TextAsync(ZipArchive zip, string name, string text, CancellationToken ct)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var target = await entry.OpenAsync(ct);
        await using var writer = new StreamWriter(target, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteAsync(text.ReplaceLineEndings("\r\n").AsMemory(), ct);
    }

    private static async Task CsvAsync<T>(ZipArchive zip, string name, IEnumerable<T> rows, CancellationToken ct, params (string Header, Func<T, object?> Value)[] columns)
    {
        var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
        await using var target = await entry.OpenAsync(ct);
        // BOM: o Excel reconhece UTF-8 (acentos) só com ele.
        await using var writer = new StreamWriter(target, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        await writer.WriteAsync(string.Join(';', columns.Select(c => c.Header)) + "\r\n");
        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            await writer.WriteAsync(string.Join(';', columns.Select(c => Cell(c.Value(row)))) + "\r\n");
        }
    }

    /// <summary>Valor de célula: aspas quando precisa e apóstrofo antes de texto que o Excel leria como fórmula.</summary>
    public static string Cell(object? value)
    {
        var text = value switch
        {
            null => string.Empty,
            decimal d => d.ToString("0.00", PtBr),
            DateTimeOffset at => Local(at).ToString("dd/MM/yyyy HH:mm:ss", PtBr),
            bool b => b ? "sim" : "não",
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToString() ?? string.Empty,
        };
        if (value is string && text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r') text = "'" + text;
        return text.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{text.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : text;
    }

    private static DateTime Local(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, StoreTimeZone).DateTime;

    private static string Condition(PartCondition condition) => condition switch
    {
        PartCondition.New => "Nova",
        PartCondition.Used => "Usada",
        PartCondition.Refurbished => "Recondicionada",
        _ => condition.ToString(),
    };

    private static string PartStatusLabel(PartStatus status) => status switch
    {
        PartStatus.Draft => "Rascunho",
        PartStatus.Active => "À venda",
        PartStatus.Inactive => "Inativa",
        _ => status.ToString(),
    };

    private static string Origin(OrderOrigin origin) => origin switch
    {
        OrderOrigin.Site => "Loja virtual",
        OrderOrigin.Counter => "Balcão",
        _ => origin.ToString(),
    };

    internal static string OrderStatusLabel(OrderStatus status) => status switch
    {
        OrderStatus.PendingPayment => "Aguardando pagamento",
        OrderStatus.Paid => "Pago",
        OrderStatus.Canceled => "Cancelado",
        OrderStatus.NeedsReview => "Em análise",
        OrderStatus.Shipped => "Enviado",
        OrderStatus.Delivered => "Entregue",
        OrderStatus.PickedUp => "Retirado",
        OrderStatus.Returned => "Devolvido",
        _ => status.ToString(),
    };
}
