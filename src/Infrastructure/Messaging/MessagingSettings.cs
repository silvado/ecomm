using System.Globalization;
using Microsoft.Extensions.Configuration;

namespace Ecommerce.Infrastructure.Messaging;

/// <summary>Ajustes da fila por configuração (testes usam intervalos curtos de nova tentativa).</summary>
public static class MessagingSettings
{
    public const string RetryDelaysKey = "Messaging:RetryDelaysSeconds";

    /// <summary>"Messaging:RetryDelaysSeconds" (lista de segundos); ausente = padrão da ADR-0002.</summary>
    public static TimeSpan[]? RetryDelays(IConfiguration configuration)
    {
        var values = configuration.GetSection(RetryDelaysKey).GetChildren()
            .Select(c => double.Parse(c.Value!, CultureInfo.InvariantCulture)).ToArray();
        return values.Length == 0 ? null : values.Select(TimeSpan.FromSeconds).ToArray();
    }
}
