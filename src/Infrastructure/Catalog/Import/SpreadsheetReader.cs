using System.IO.Compression;
using System.Text;
using System.Xml;

namespace Ecommerce.Infrastructure.Catalog.Import;

/// <summary>Arquivo que não dá para ler como planilha; a mensagem vai para o usuário.</summary>
public sealed class SpreadsheetException(string message) : Exception(message);

/// <summary>
/// Lê a planilha enviada (RF11) como linhas de texto: XLSX (primeira aba, sem biblioteca — o formato é um ZIP de XMLs,
/// ECMA-376) ou CSV (separador ";", "," ou tab, detectado pelo cabeçalho; UTF-8 ou Windows-1252, que é como o Excel em
/// português salva "CSV"). Cada linha vem com o número que o usuário vê na planilha.
/// </summary>
public static class SpreadsheetReader
{
    public sealed record Row(int Line, IReadOnlyList<string> Cells);

    /// <summary>Teto do XML descompactado: arquivo pequeno que "explode" ao abrir é recusado.</summary>
    private const long MaxUncompressedBytes = 64L * 1024 * 1024;

    private static readonly XmlReaderSettings Xml = new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreWhitespace = false };

    static SpreadsheetReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static bool IsXlsx(ReadOnlySpan<byte> content) => content.Length >= 4 && content[..4].SequenceEqual("PK\u0003\u0004"u8);

    /// <exception cref="SpreadsheetException">Arquivo que não é planilha válida.</exception>
    public static IReadOnlyList<Row> Read(byte[] content) => IsXlsx(content) ? ReadXlsx(content) : ReadCsv(content);

    public static IReadOnlyList<Row> ReadCsv(byte[] content)
    {
        var text = Decode(content);
        var delimiter = DetectDelimiter(text);
        var rows = new List<Row>();
        var cells = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        var line = 1;
        var rowLine = 1;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else
                {
                    if (c == '\n') line++;
                    cell.Append(c);
                }
                continue;
            }
            if (c == '"' && cell.Length == 0) quoted = true;
            else if (c == delimiter) { cells.Add(cell.ToString()); cell.Clear(); }
            else if (c is '\r' or '\n')
            {
                if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                cells.Add(cell.ToString());
                cell.Clear();
                rows.Add(new Row(rowLine, cells));
                cells = [];
                rowLine = ++line;
            }
            else cell.Append(c);
        }
        if (cell.Length > 0 || cells.Count > 0)
        {
            cells.Add(cell.ToString());
            rows.Add(new Row(rowLine, cells));
        }
        return rows;
    }

    public static IReadOnlyList<Row> ReadXlsx(byte[] content)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(content), ZipArchiveMode.Read);
            var sheetPath = FirstSheetPath(zip);
            var shared = SharedStrings(zip);
            return SheetRows(Entry(zip, sheetPath) ?? throw Invalid(), shared);
        }
        catch (Exception e) when (e is InvalidDataException or XmlException or IOException or FormatException or ArgumentException)
        {
            throw Invalid();
        }
    }

    private static string Decode(byte[] content)
    {
        try
        {
            var text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(content);
            return text.Length > 0 && text[0] == '﻿' ? text[1..] : text;
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(1252).GetString(content);
        }
    }

    /// <summary>O separador que mais aparece fora de aspas na primeira linha.</summary>
    private static char DetectDelimiter(string text)
    {
        var counts = new Dictionary<char, int> { [';'] = 0, [','] = 0, ['\t'] = 0 };
        var quoted = false;
        foreach (var c in text)
        {
            if (c == '"') quoted = !quoted;
            else if (!quoted && c is '\r' or '\n') break;
            else if (!quoted && counts.TryGetValue(c, out var count)) counts[c] = count + 1;
        }
        return counts.MaxBy(p => p.Value) is { Value: > 0 } best ? best.Key : ';';
    }

    private static string FirstSheetPath(ZipArchive zip)
    {
        string? relationId = null;
        using (var reader = Open(Entry(zip, "xl/workbook.xml") ?? throw Invalid()))
            while (reader.Read())
                if (reader is { NodeType: XmlNodeType.Element, LocalName: "sheet" })
                {
                    relationId = reader.GetAttribute("id", "http://schemas.openxmlformats.org/officeDocument/2006/relationships");
                    break;
                }
        if (relationId is null) throw Invalid();

        using (var reader = Open(Entry(zip, "xl/_rels/workbook.xml.rels") ?? throw Invalid()))
            while (reader.Read())
                if (reader is { NodeType: XmlNodeType.Element, LocalName: "Relationship" } && reader.GetAttribute("Id") == relationId)
                {
                    var target = reader.GetAttribute("Target") ?? throw Invalid();
                    return target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
                }
        throw Invalid();
    }

    private static List<string> SharedStrings(ZipArchive zip)
    {
        var strings = new List<string>();
        if (Entry(zip, "xl/sharedStrings.xml") is not { } entry) return strings;
        using var reader = Open(entry);
        StringBuilder? current = null;
        while (reader.Read())
        {
            if (reader is { NodeType: XmlNodeType.Element, LocalName: "si" }) current = new StringBuilder();
            else if (reader is { NodeType: XmlNodeType.Element, LocalName: "t" } && current is not null) current.Append(reader.ReadElementContentAsString());
            else if (reader is { NodeType: XmlNodeType.EndElement, LocalName: "si" } && current is not null)
            {
                strings.Add(current.ToString());
                current = null;
            }
            // "rPh" (guia fonética) também tem <t>, mas não faz parte do texto.
            else if (reader is { NodeType: XmlNodeType.Element, LocalName: "rPh" }) reader.Skip();
        }
        return strings;
    }

    private static List<Row> SheetRows(ZipArchiveEntry sheet, List<string> shared)
    {
        var rows = new List<Row>();
        using var reader = Open(sheet);
        List<string>? cells = null;
        var rowNumber = 0;
        while (reader.Read())
        {
            if (reader is { NodeType: XmlNodeType.Element, LocalName: "row" })
            {
                rowNumber = int.TryParse(reader.GetAttribute("r"), out var r) ? r : rowNumber + 1;
                cells = [];
                if (reader.IsEmptyElement) { rows.Add(new Row(rowNumber, cells)); cells = null; }
            }
            else if (reader is { NodeType: XmlNodeType.EndElement, LocalName: "row" } && cells is not null)
            {
                rows.Add(new Row(rowNumber, cells));
                cells = null;
            }
            else if (reader is { NodeType: XmlNodeType.Element, LocalName: "c" } && cells is not null)
            {
                var column = ColumnIndex(reader.GetAttribute("r")) ?? cells.Count;
                var type = reader.GetAttribute("t");
                var value = reader.IsEmptyElement ? string.Empty : CellValue(reader.ReadSubtree(), type, shared);
                while (cells.Count < column) cells.Add(string.Empty);
                if (cells.Count == column) cells.Add(value);
                else cells[column] = value;
            }
        }
        return rows;
    }

    private static string CellValue(XmlReader cell, string? type, List<string> shared)
    {
        using (cell)
        {
            var text = new StringBuilder();
            string? raw = null;
            while (cell.Read())
            {
                if (cell is { NodeType: XmlNodeType.Element, LocalName: "v" }) raw = cell.ReadElementContentAsString();
                if (cell is { NodeType: XmlNodeType.Element, LocalName: "t" }) text.Append(cell.ReadElementContentAsString());
            }
            return type switch
            {
                "s" when int.TryParse(raw, out var index) && index >= 0 && index < shared.Count => shared[index],
                "s" => throw Invalid(),
                "inlineStr" => text.ToString(),
                "b" => raw == "1" ? "VERDADEIRO" : "FALSO",
                _ => raw ?? string.Empty,
            };
        }
    }

    /// <summary>"B12" → 1 (coluna B).</summary>
    private static int? ColumnIndex(string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return null;
        var index = 0;
        var letters = 0;
        foreach (var c in reference)
        {
            if (c is < 'A' or > 'Z') break;
            index = index * 26 + (c - 'A' + 1);
            if (++letters > 3) return null;
        }
        return letters == 0 ? null : index - 1;
    }

    private static ZipArchiveEntry? Entry(ZipArchive zip, string path) =>
        zip.GetEntry(path) is { } entry
            ? entry.Length > MaxUncompressedBytes ? throw new SpreadsheetException("Planilha grande demais depois de aberta.") : entry
            : null;

    private static XmlReader Open(ZipArchiveEntry entry) => XmlReader.Create(entry.Open(), Xml);

    private static SpreadsheetException Invalid() => new("Não foi possível ler o arquivo: envie a planilha em XLSX ou CSV.");
}
