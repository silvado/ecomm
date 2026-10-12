using Ecommerce.Application.Common;
using Microsoft.Extensions.Configuration;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Ecommerce.Infrastructure.Logging;

/// <summary>
/// Logs estruturados (RNF06, CLAUDE.md): JSON no console, com <c>tenant_id</c> e <c>correlation_id</c> em toda linha e
/// redação de campos sensíveis. A seção "Serilog" da configuração ajusta níveis sem mudar código.
/// </summary>
public static class LoggingConfiguration
{
    public static LoggerConfiguration ConfigureEcommerce(this LoggerConfiguration logger, IConfiguration configuration) =>
        logger
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore", LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", LogEventLevel.Warning)
            .ReadFrom.Configuration(configuration)
            .Enrich.FromLogContext()
            .Enrich.With<DiagnosticContextEnricher>()
            .Enrich.With<RedactionEnricher>()
            .WriteTo.Console(new RenderedCompactJsonFormatter());
}

/// <summary>Loja e correlação do fluxo atual (<see cref="Correlation"/>) em todo log.</summary>
public sealed class DiagnosticContextEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        if (Correlation.TenantId is { } tenantId) logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("tenant_id", tenantId));
        if (Correlation.Current is { } correlation) logEvent.AddPropertyIfAbsent(propertyFactory.CreateProperty("correlation_id", correlation));
    }
}

/// <summary>
/// Redação (RNF05): propriedade com nome sensível (senha, token, segredo, CPF, placa…) vira "***"; texto livre passa
/// pela mesma redação do log de integração (tokens, chaves, CPF/CNPJ e e-mails).
/// </summary>
public sealed class RedactionEnricher : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory propertyFactory)
    {
        foreach (var (name, value) in logEvent.Properties.ToList())
        {
            var redacted = Redact(name, value);
            if (!ReferenceEquals(redacted, value)) logEvent.AddOrUpdateProperty(new LogEventProperty(name, redacted));
        }
    }

    private static LogEventPropertyValue Redact(string name, LogEventPropertyValue value)
    {
        if (name is "tenant_id" or "correlation_id") return value;
        if (Redaction.IsSensitiveName(name)) return new ScalarValue(Redaction.Mask);
        return value switch
        {
            ScalarValue { Value: string text } when Redaction.Text(text, int.MaxValue) is { } clean && clean != text => new ScalarValue(clean),
            StructureValue structure when structure.Properties.Any(p => !ReferenceEquals(Redact(p.Name, p.Value), p.Value)) =>
                new StructureValue(structure.Properties.Select(p => new LogEventProperty(p.Name, Redact(p.Name, p.Value))), structure.TypeTag),
            DictionaryValue dictionary when dictionary.Elements.Any(e => !ReferenceEquals(Redact(e.Key.Value?.ToString() ?? "", e.Value), e.Value)) =>
                new DictionaryValue(dictionary.Elements.Select(e => new KeyValuePair<ScalarValue, LogEventPropertyValue>(e.Key, Redact(e.Key.Value?.ToString() ?? "", e.Value)))),
            _ => value,
        };
    }
}
