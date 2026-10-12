using Ecommerce.Application.Common;
using Ecommerce.Infrastructure.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Ecommerce.Integration.Tests.Logging;

/// <summary>Logs estruturados (RNF06): tenant e correlação em toda linha e redação de dados sensíveis (RNF05).</summary>
public sealed class LoggingTests
{
    private sealed class Collect : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (ILogger Logger, Collect Sink) Logger()
    {
        var sink = new Collect();
        var logger = new LoggerConfiguration().Enrich.With<DiagnosticContextEnricher>().Enrich.With<RedactionEnricher>().WriteTo.Sink(sink).CreateLogger();
        return (logger, sink);
    }

    [Fact]
    public void Campos_sensiveis_e_textos_com_segredos_sao_redigidos()
    {
        var (logger, sink) = Logger();

        logger.Information("Login de {Email} com {Password} via {Path} e {Request}", "maria@exemplo.com.br", "senha-forte-123",
            "/api/x?access_token=APP_USR-abc123def456", new { AccessToken = "APP_USR-xyz", OrderId = 42 });

        var e = Assert.Single(sink.Events);
        var rendered = e.RenderMessage(System.Globalization.CultureInfo.InvariantCulture);
        Assert.DoesNotContain("maria@exemplo.com.br", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("senha-forte-123", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("APP_USR", rendered, StringComparison.Ordinal);
        Assert.Contains("42", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tenant_e_correlacao_do_fluxo_entram_em_todo_log()
    {
        var (logger, sink) = Logger();
        var tenant = Guid.CreateVersion7();

        await Task.Run(() =>
        {
            Correlation.TenantId = tenant;
            Correlation.Current = "req-123";
            logger.Information("dentro do fluxo");
        });
        logger.Information("fora do fluxo");

        Assert.Equal(tenant.ToString(), sink.Events[0].Properties["tenant_id"].ToString());
        Assert.Equal("\"req-123\"", sink.Events[0].Properties["correlation_id"].ToString());
        Assert.False(sink.Events[1].Properties.ContainsKey("correlation_id"));
    }
}
