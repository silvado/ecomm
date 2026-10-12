using System.Diagnostics;
using Ecommerce.Application.Common;
using Wolverine;

namespace Ecommerce.Api.Diagnostics;

/// <summary>
/// Correlação da requisição (RNF06, RF29): vem do cabeçalho <c>X-Correlation-Id</c> (saneado) ou do trace atual, volta na
/// resposta e segue para as mensagens que a requisição gerar — logs, log de integração e falhas ficam ligados por ela.
/// </summary>
public static class CorrelationMiddleware
{
    public const string Header = "X-Correlation-Id";

    public static IApplicationBuilder UseCorrelation(this IApplicationBuilder app) => app.Use(async (http, next) =>
    {
        var id = Correlation.Sanitize(http.Request.Headers[Header].ToString())
            ?? Correlation.Sanitize(Activity.Current?.TraceId.ToString())
            ?? Guid.CreateVersion7().ToString("N");
        Correlation.Current = id;
        http.Response.Headers[Header] = id;
        if (http.RequestServices.GetService<IMessageContext>() is { } messages) messages.CorrelationId = id;
        await next(http);
    });
}
