using System.IO.Compression;
using System.Text;
using Ecommerce.Domain.Catalog;
using Ecommerce.Infrastructure.Catalog.Import;

namespace Ecommerce.Integration.Tests.Catalog;

/// <summary>Leitura e validação da planilha de peças (RF11), sem banco.</summary>
public sealed class PartSpreadsheetTests
{
    private static PartSpreadsheet.ParseResult Csv(string text, Encoding? encoding = null) =>
        PartSpreadsheet.Parse(SpreadsheetReader.Read((encoding ?? Encoding.UTF8).GetBytes(text)));

    [Fact]
    public void Csv_do_excel_em_portugues_com_acentos_aspas_e_valores_em_reais()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var text = "Código;Título;Descrição;Estado;Preço (R$);Quantidade;Peso (g);OEM\r\n"
            + "k-1;Farol \"Gol\";\"Lente boa;\nsem trincas\";Usada;R$ 1.234,56;2;3500;5U0 941-015 | 5u0941015a\r\n"
            + ";;;;;;;\r\n"
            + "K-2;Retrovisor;;Nova;180.5;0;;\r\n";

        var result = Csv(text, Encoding.GetEncoding(1252));

        Assert.Empty(result.Issues);
        Assert.Equal(2, result.TotalRows);
        var farol = result.Rows[0];
        Assert.Equal((2, "K-1", "Farol \"Gol\"", "Lente boa;\nsem trincas", PartCondition.Used, 1234.56m, 2, 3500),
            (farol.Line, farol.InternalCode, farol.Title, farol.Description, farol.Condition, farol.Price, farol.Quantity, farol.WeightG));
        Assert.Equal(["5U0941015", "5U0941015A"], farol.OemCodes);
        Assert.Equal((5, PartCondition.New, 180.5m, null), (result.Rows[1].Line, result.Rows[1].Condition, result.Rows[1].Price, result.Rows[1].Description));
    }

    [Fact]
    public void Separador_virgula_e_bom_utf8_sao_reconhecidos()
    {
        var result = Csv("﻿codigo,titulo,estado,preco,quantidade\nK-1,Farol,usado,\"350,00\",1\n");

        Assert.Empty(result.Issues);
        Assert.Equal(350m, Assert.Single(result.Rows).Price);
    }

    [Fact]
    public void Cada_linha_errada_aparece_no_relatorio_com_o_motivo()
    {
        var result = Csv("codigo;titulo;estado;preco;quantidade;comprimento_cm\n"
            + "K-1;Farol;usada;100;1;\n"
            + "K-1;Farol de novo;usada;100;1;\n"
            + ";Sem código;usada;100;1;\n"
            + "K-3;;usada;100;1;\n"
            + "K-4;Peça;seminova;100;1;\n"
            + "K-5;Peça;usada;abc;1;\n"
            + "K-6;Peça;usada;100;-1;\n"
            + "K-7;Peça;usada;100;1;12,5\n"
            + "K-8;Peça;usada;0,001;1;\n");

        Assert.Equal(9, result.TotalRows);
        Assert.Equal("K-1", Assert.Single(result.Rows).InternalCode);
        Assert.Equal([3, 4, 5, 6, 7, 8, 9, 10], result.Issues.Select(i => i.Line));
        Assert.Contains("repetido na planilha (já está na linha 2)", result.Issues[0].Message, StringComparison.Ordinal);
        Assert.Contains("Estado inválido", result.Issues[3].Message, StringComparison.Ordinal);
        Assert.Contains("Comprimento precisa ser um número inteiro", result.Issues[6].Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Faltando_coluna_obrigatoria_nada_e_lido()
    {
        var result = Csv("codigo;titulo;preco\nK-1;Farol;100\n");

        Assert.Equal(0, result.TotalRows);
        Assert.Contains("estado, quantidade", Assert.Single(result.Issues).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Planilha_acima_do_limite_de_linhas_e_recusada()
    {
        var text = new StringBuilder("codigo;titulo;estado;preco;quantidade\n");
        for (var i = 0; i <= PartImport.MaxRows; i++) text.Append(System.Globalization.CultureInfo.InvariantCulture, $"K-{i};Peça;usada;10;1\n");

        var result = Csv(text.ToString());

        Assert.Empty(result.Rows);
        Assert.Contains($"No máximo {PartImport.MaxRows}", Assert.Single(result.Issues).Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Modelos_em_csv_e_xlsx_sao_lidos_de_volta_sem_erros()
    {
        foreach (var template in new[] { PartSpreadsheet.TemplateCsv(), PartSpreadsheet.TemplateXlsx() })
        {
            var result = PartSpreadsheet.Parse(SpreadsheetReader.Read(template));
            Assert.Empty(result.Issues);
            Assert.Equal(["K-0001", "K-0002"], result.Rows.Select(r => r.InternalCode));
            Assert.Equal((350m, 180.5m), (result.Rows[0].Price, result.Rows[1].Price));
        }
    }

    [Fact]
    public void Xlsx_do_excel_com_textos_compartilhados_numeros_e_celulas_puladas()
    {
        var shared = new[] { "codigo", "titulo", "estado", "preco", "quantidade", "K-9", "Para-choque traseiro", "usada" };
        var sheet = """
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"><sheetData>
            <row r="1"><c r="A1" t="s"><v>0</v></c><c r="B1" t="s"><v>1</v></c><c r="C1" t="s"><v>2</v></c><c r="D1" t="s"><v>3</v></c><c r="E1" t="s"><v>4</v></c></row>
            <row r="3"><c r="A3" t="s"><v>5</v></c><c r="B3" t="s"><v>6</v></c><c r="C3" t="s"><v>7</v></c><c r="D3"><v>420.9</v></c><c r="E3"><v>3</v></c></row>
            </sheetData></worksheet>
            """;
        var result = PartSpreadsheet.Parse(SpreadsheetReader.Read(Xlsx(sheet, shared)));

        Assert.Empty(result.Issues);
        var row = Assert.Single(result.Rows);
        Assert.Equal((3, "K-9", "Para-choque traseiro", 420.9m, 3), (row.Line, row.InternalCode, row.Title, row.Price, row.Quantity));
    }

    [Fact]
    public void Arquivo_que_nao_e_planilha_e_recusado()
    {
        Assert.Throws<SpreadsheetException>(() => SpreadsheetReader.Read([.. "PK\u0003\u0004"u8, 1, 2, 3, 4, 5]));

        var result = PartSpreadsheet.Parse(SpreadsheetReader.Read([0xFF, 0xD8, 0xFF, 0xE0, 0x00]));
        Assert.Equal(0, result.TotalRows);
        Assert.NotEmpty(result.Issues);
    }

    private static byte[] Xlsx(string sheet, string[] shared)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string path, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(xml);
            }
            Add("xl/workbook.xml", """<workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships"><sheets><sheet name="Plan1" sheetId="1" r:id="rId3"/></sheets></workbook>""");
            Add("xl/_rels/workbook.xml.rels", """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId3" Type="worksheet" Target="worksheets/plan1.xml"/></Relationships>""");
            Add("xl/sharedStrings.xml", "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">"
                + string.Concat(shared.Select(s => $"<si><r><t>{s[..1]}</t></r><r><t>{s[1..]}</t></r></si>")) + "</sst>");
            Add("xl/worksheets/plan1.xml", sheet);
        }
        return buffer.ToArray();
    }
}
