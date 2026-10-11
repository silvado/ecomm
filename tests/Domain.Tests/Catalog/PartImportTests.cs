using Ecommerce.Domain.Catalog;

namespace Ecommerce.Domain.Tests.Catalog;

/// <summary>Importação por planilha (RF11): prévia, confirmação e lotes com progresso.</summary>
public sealed class PartImportTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 11, 12, 0, 0, TimeSpan.Zero);

    private static PartImportRow Row(int line, string code) =>
        new(line, code, "Farol", null, PartCondition.Used, 100m, 1, null, null, null, null, []);

    private static PartImport Import(int rows, int errors = 0) =>
        PartImport.Validate(Guid.CreateVersion7(), Guid.CreateVersion7(), " pecas.xlsx ", rows + errors,
            Enumerable.Range(2, rows).Select(i => Row(i, $"K-{i}")).ToList(),
            Enumerable.Range(0, errors).Select(i => new PartImportIssue(1000 + i, "Preço inválido.")).ToList(), 0, Now);

    [Fact]
    public void Previa_guarda_linhas_validas_e_erros_sem_comecar()
    {
        var import = Import(rows: 3, errors: 2);

        Assert.Equal((PartImportStatus.Validated, "pecas.xlsx", 5, 3, 2), (import.Status, import.FileName, import.TotalRows, import.ValidRows, import.ErrorCount));
        Assert.Empty(import.NextBatch(10));
    }

    [Fact]
    public void Relatorio_lista_ate_o_limite_mas_conta_todos_os_erros()
    {
        var import = Import(rows: 0, errors: PartImport.MaxListedIssues + 7);

        Assert.Equal((PartImport.MaxListedIssues + 7, PartImport.MaxListedIssues), (import.ErrorCount, import.Errors.Count));
    }

    [Fact]
    public void Codigo_repetido_entre_linhas_validas_e_recusado() =>
        Assert.Throws<ArgumentException>(() => PartImport.Validate(Guid.CreateVersion7(), Guid.CreateVersion7(), "a.csv", 2,
            [Row(2, "K-1"), Row(3, "K-1")], [], 0, Now));

    [Fact]
    public void Confirmar_uma_vez_e_cadastrar_em_lotes_ate_concluir()
    {
        var import = Import(rows: 5);

        Assert.True(import.Confirm(Now));
        Assert.False(import.Confirm(Now.AddSeconds(1)));
        Assert.Equal((PartImportStatus.Processing, Now), (import.Status, import.StartedAt));

        var first = import.NextBatch(2);
        Assert.Equal([2, 3], first.Select(r => r.Line));
        import.RecordBatch(2, 1, [new PartImportSkip(3, "K-3")], [], Now);
        Assert.Equal([4, 5], import.NextBatch(2).Select(r => r.Line));

        import.RecordBatch(2, 1, [], [new PartImportIssue(5, "Título obrigatório.")], Now);
        import.RecordBatch(1, 1, [], [], Now.AddMinutes(1));

        Assert.Equal((PartImportStatus.Completed, 5, 3, 1, 1), (import.Status, import.ProcessedRows, import.ImportedCount, import.Skipped.Count, import.ErrorCount));
        Assert.Equal(Now.AddMinutes(1), import.FinishedAt);
        Assert.Empty(import.NextBatch(2));
        Assert.Throws<InvalidOperationException>(() => import.RecordBatch(1, 1, [], [], Now));
    }

    [Fact]
    public void Lote_inconsistente_e_recusado_sem_mudar_o_progresso()
    {
        var import = Import(rows: 2);
        import.Confirm(Now);

        Assert.Throws<ArgumentException>(() => import.RecordBatch(3, 3, [], [], Now));
        Assert.Throws<ArgumentException>(() => import.RecordBatch(2, 1, [], [], Now));
        Assert.Equal(0, import.ProcessedRows);
    }

    [Fact]
    public void Sem_linhas_validas_conclui_ao_confirmar()
    {
        var import = Import(rows: 0, errors: 3);

        Assert.True(import.Confirm(Now));
        Assert.Equal(PartImportStatus.Completed, import.Status);
    }
}
