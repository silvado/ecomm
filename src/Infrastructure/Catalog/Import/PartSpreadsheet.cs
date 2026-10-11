using System.Globalization;
using System.IO.Compression;
using System.Security;
using System.Text;
using Ecommerce.Application.Catalog;
using Ecommerce.Domain.Catalog;

namespace Ecommerce.Infrastructure.Catalog.Import;

/// <summary>
/// Planilha de peças (RF11): colunas do modelo, validação linha a linha com as regras do cadastro (RF08) e o arquivo
/// modelo em CSV e XLSX. Cabeçalhos são reconhecidos sem acento, maiúsculas, espaços ou "_"; a ordem das colunas não importa.
/// </summary>
public static class PartSpreadsheet
{
    public sealed record ParseResult(int TotalRows, IReadOnlyList<PartImportRow> Rows, IReadOnlyList<PartImportIssue> Issues);

    private enum Column { Code, Title, Description, Condition, Price, Quantity, Length, Width, Height, Weight, Oem }

    /// <summary>Cabeçalho do modelo, na ordem, e os nomes aceitos de cada coluna.</summary>
    private static readonly (Column Column, string Header, bool Required, string[] Aliases)[] Columns =
    [
        (Column.Code, "codigo", true, ["codigo", "codigointerno", "cod"]),
        (Column.Title, "titulo", true, ["titulo", "nome"]),
        (Column.Description, "descricao", false, ["descricao"]),
        (Column.Condition, "estado", true, ["estado", "condicao"]),
        (Column.Price, "preco", true, ["preco", "precor", "valor", "valorr"]),
        (Column.Quantity, "quantidade", true, ["quantidade", "qtd", "qtde", "estoque"]),
        (Column.Length, "comprimento_cm", false, ["comprimentocm", "comprimento"]),
        (Column.Width, "largura_cm", false, ["larguracm", "largura"]),
        (Column.Height, "altura_cm", false, ["alturacm", "altura"]),
        (Column.Weight, "peso_g", false, ["pesog", "peso"]),
        (Column.Oem, "oem", false, ["oem", "codigosoem", "codigooem"]),
    ];

    private static readonly Dictionary<string, PartCondition> Conditions = new(StringComparer.Ordinal)
    {
        ["usada"] = PartCondition.Used, ["usado"] = PartCondition.Used, ["used"] = PartCondition.Used,
        ["nova"] = PartCondition.New, ["novo"] = PartCondition.New, ["new"] = PartCondition.New,
        ["recondicionada"] = PartCondition.Refurbished, ["recondicionado"] = PartCondition.Refurbished, ["refurbished"] = PartCondition.Refurbished,
    };

    private static readonly string[][] Examples =
    [
        ["K-0001", "Farol dianteiro esquerdo Gol G5", "Original, lente sem trincas.", "usada", "350,00", "1", "45", "30", "25", "3500", "5U0941015 | 5U0941015A"],
        ["K-0002", "Retrovisor direito elétrico Onix", "", "usada", "180,50", "2", "30", "20", "20", "1200", ""],
    ];

    public static ParseResult Parse(IReadOnlyList<SpreadsheetReader.Row> sheet)
    {
        var rows = sheet.Where(r => r.Cells.Any(c => !string.IsNullOrWhiteSpace(c))).ToList();
        if (rows.Count == 0) return File("A planilha está vazia.");

        var header = rows[0];
        var map = new Dictionary<Column, int>();
        for (var i = 0; i < header.Cells.Count; i++)
        {
            var name = Key(header.Cells[i]);
            if (Columns.FirstOrDefault(c => c.Aliases.Contains(name)) is { Header: not null } known && !map.ContainsKey(known.Column))
                map[known.Column] = i;
        }
        var missing = Columns.Where(c => c.Required && !map.ContainsKey(c.Column)).Select(c => c.Header).ToList();
        if (missing.Count > 0)
            return File($"Faltam colunas obrigatórias no cabeçalho (linha {header.Line}): {string.Join(", ", missing)}. Use o modelo.");

        var data = rows.Skip(1).ToList();
        if (data.Count == 0) return File("A planilha não tem linhas de peças abaixo do cabeçalho.");
        if (data.Count > PartImport.MaxRows) return new ParseResult(data.Count, [], [new(0, $"No máximo {PartImport.MaxRows} peças por planilha; divida o arquivo.")]);

        var valid = new List<PartImportRow>();
        var issues = new List<PartImportIssue>();
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var validator = Guid.CreateVersion7();
        foreach (var row in data)
        {
            string Cell(Column column) => map.TryGetValue(column, out var i) && i < row.Cells.Count ? row.Cells[i].Trim() : string.Empty;
            try
            {
                var code = Part.NormalizeInternalCode(Cell(Column.Code));
                if (seen.TryGetValue(code, out var first)) throw new ArgumentException($"Código {code} repetido na planilha (já está na linha {first}).");
                var condition = Conditions.GetValueOrDefault(Key(Cell(Column.Condition)), (PartCondition)(-1));
                if (condition < 0) throw new ArgumentException("Estado inválido: use usada, nova ou recondicionada.");
                var price = Money(Cell(Column.Price)) ?? throw new ArgumentException("Preço inválido: use, por exemplo, 350,00.");
                var quantity = Integer(Cell(Column.Quantity), "Quantidade") ?? throw new ArgumentException("Informe a quantidade (0 ou mais).");
                if (quantity is < 0 or > PartCommandResult.MaxQuantity)
                    throw new ArgumentException($"Quantidade precisa estar entre 0 e {PartCommandResult.MaxQuantity}.");
                var oem = Cell(Column.Oem).Split(['|', ';', ',', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                var line = new PartImportRow(row.Line, code, Cell(Column.Title), NullIfEmpty(Cell(Column.Description)), condition, price, quantity,
                    Integer(Cell(Column.Length), "Comprimento"), Integer(Cell(Column.Width), "Largura"), Integer(Cell(Column.Height), "Altura"),
                    Integer(Cell(Column.Weight), "Peso"), oem);
                // Mesmas regras do cadastro manual (título, descrição, preço, medidas, OEM).
                var part = Part.Create(validator, code, line.Details(), DateTimeOffset.UnixEpoch);
                valid.Add(line with { Title = part.Title, Description = NullIfEmpty(part.Description), OemCodes = part.OemCodes.Select(o => o.Code).ToList() });
                seen[code] = row.Line;
            }
            catch (ArgumentException e)
            {
                issues.Add(new PartImportIssue(row.Line, e.Message.Split(" (Parameter", 2)[0]));
            }
        }
        return new ParseResult(data.Count, valid, issues);
    }

    public static byte[] TemplateCsv()
    {
        var text = new StringBuilder();
        text.AppendJoin(';', Columns.Select(c => c.Header)).Append("\r\n");
        foreach (var example in Examples) text.AppendJoin(';', example.Select(CsvCell)).Append("\r\n");
        // BOM: o Excel em português só reconhece UTF-8 (acentos) com ele.
        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    /// <summary>XLSX mínimo (uma aba, textos em linha): abre no Excel, LibreOffice e Google Planilhas.</summary>
    public static byte[] TemplateXlsx()
    {
        var sheet = new StringBuilder("""<?xml version="1.0" encoding="UTF-8" standalone="yes"?><worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>""");
        string[][] lines = [Columns.Select(c => c.Header).ToArray(), .. Examples];
        for (var r = 0; r < lines.Length; r++)
        {
            sheet.Append(CultureInfo.InvariantCulture, $"""<row r="{r + 1}">""");
            for (var c = 0; c < lines[r].Length; c++)
                sheet.Append(CultureInfo.InvariantCulture, $"""<c r="{(char)('A' + c)}{r + 1}" t="inlineStr"><is><t xml:space="preserve">{SecurityElement.Escape(lines[r][c])}</t></is></c>""");
            sheet.Append("</row>");
        }
        sheet.Append("</sheetData></worksheet>");

        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            Add(zip, "[Content_Types].xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Types xmlns="http://schemas.openxmlformats.org/package/2006/content-types"><Default Extension="rels" ContentType="application/vnd.openxmlformats-package.relationships+xml"/><Default Extension="xml" ContentType="application/xml"/><Override PartName="/xl/workbook.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml"/><Override PartName="/xl/worksheets/sheet1.xml" ContentType="application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml"/></Types>""");
            Add(zip, "_rels/.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument" Target="xl/workbook.xml"/></Relationships>""");
            Add(zip, "xl/workbook.xml", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Peças" sheetId="1" r:id="rId1"/></sheets></workbook>""");
            Add(zip, "xl/_rels/workbook.xml.rels", """<?xml version="1.0" encoding="UTF-8" standalone="yes"?><Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/></Relationships>""");
            Add(zip, "xl/worksheets/sheet1.xml", sheet.ToString());
        }
        return buffer.ToArray();
    }

    private static void Add(ZipArchive zip, string path, string xml)
    {
        using var writer = new StreamWriter(zip.CreateEntry(path, CompressionLevel.Optimal).Open(), new UTF8Encoding(false));
        writer.Write(xml);
    }

    private static string CsvCell(string value) =>
        value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"" : value;

    private static ParseResult File(string message) => new(0, [], [new PartImportIssue(0, message)]);

    private static string? NullIfEmpty(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>"Preço (R$)" → "precor": sem acento, minúsculas, só letras e dígitos.</summary>
    private static string Key(string value)
    {
        var plain = new StringBuilder();
        foreach (var c in value.Normalize(NormalizationForm.FormD))
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark && char.IsAsciiLetterOrDigit(c))
                plain.Append(char.ToLowerInvariant(c));
        return plain.ToString();
    }

    /// <summary>"R$ 1.234,56", "1234,56" e "1234.56" (como o XLSX guarda números).</summary>
    private static decimal? Money(string value)
    {
        var text = value.Replace("R$", "", StringComparison.OrdinalIgnoreCase).Replace(" ", "", StringComparison.Ordinal).Replace(" ", "", StringComparison.Ordinal);
        if (text.Length == 0) return null;
        if (text.Contains(',', StringComparison.Ordinal)) text = text.Replace(".", "", StringComparison.Ordinal).Replace(',', '.');
        return decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    /// <summary>Inteiro opcional; aceita "30" e "30,0"/"30.0" (número inteiro salvo como decimal).</summary>
    private static int? Integer(string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Replace(',', '.');
        if (decimal.TryParse(text, NumberStyles.AllowDecimalPoint | NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed)
            && parsed == decimal.Truncate(parsed) && parsed is >= int.MinValue and <= int.MaxValue)
            return (int)parsed;
        throw new ArgumentException($"{name} precisa ser um número inteiro.");
    }
}
