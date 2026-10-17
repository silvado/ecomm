using Ecommerce.Application.Catalog;
using Ecommerce.Application.Common;
using Ecommerce.Application.Email;
using Ecommerce.Application.Orders;
using Ecommerce.Application.Payments;
using Ecommerce.Domain.Integrations;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Wolverine;
using Wolverine.Runtime;

namespace Ecommerce.Infrastructure.Integrations;

/// <summary>
/// Última etapa da política de erro das mensagens (RF28 CA2): esgotadas as tentativas, a falha é registrada para a loja
/// (visível no painel, com motivo redigido) e a mensagem vai para a fila de falhas do Wolverine, de onde pode ser
/// reprocessada (CA3). A plataforma é alertada por log crítico.
/// </summary>
public static partial class MessageFailures
{
    private static readonly Dictionary<Type, string> Descriptions = new()
    {
        [typeof(ProcessPaymentNotification)] = "Confirmar pagamento no gateway",
        [typeof(RecordPaymentNotification)] = "Registrar aviso de pagamento",
        [typeof(ExpireOrder)] = "Cancelar pedido sem pagamento no prazo",
        [typeof(StartPartImport)] = "Iniciar importação de peças",
        [typeof(ProcessPartImport)] = "Importar peças da planilha (lote)",
        [typeof(NotifyBuyer)] = "Enviar e-mail do pedido ao comprador",
        [typeof(SendCustomerAccessLink)] = "Enviar link de acesso aos pedidos",
        [typeof(Application.Integrations.RouteChannelEvent)] = "Rotear aviso de canal para a loja",
        [typeof(Application.Integrations.ChannelEventReceived)] = "Registrar aviso de canal na loja",
        [typeof(Application.Integrations.ProcessChannelEvent)] = "Processar aviso de canal",
        [typeof(Application.Channels.SyncListing)] = "Atualizar anúncio no canal",
        [typeof(Application.Channels.ReconcileChannels)] = "Reconciliar anúncios com os canais",
        [typeof(Application.Channels.RenewChannelCredentials)] = "Renovar o acesso aos canais de venda",
        [typeof(Application.Channels.ReconcileListing)] = "Conferir anúncio no canal",
        [typeof(Application.Inventory.StockChanged)] = "Avisar os canais da mudança de estoque",
        [typeof(Application.Fiscal.SendInvoice)] = "Enviar NF-e ao provedor fiscal",
        [typeof(Application.Fiscal.CheckInvoice)] = "Consultar a NF-e no provedor fiscal",
    };

    public static string Describe(object? message) =>
        message is null ? "Processamento em segundo plano"
        : Descriptions.TryGetValue(message.GetType(), out var description) ? description
        : $"Processar {message.GetType().Name}";

    public static async ValueTask RecordAndDeadLetterAsync(IWolverineRuntime runtime, IEnvelopeLifecycle lifecycle, Exception exception)
    {
        var envelope = lifecycle.Envelope;
        var logger = runtime.Services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(MessageFailures));
        var description = Describe(envelope?.Message);
        LogFailure(logger, exception, description, envelope?.TenantId, envelope?.Attempts ?? 0);

        if (envelope is not null && Guid.TryParse(envelope.TenantId, out var tenantId))
        {
            try
            {
                await using var scope = runtime.Services.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<TenantScope>().Set(tenantId);
                var db = scope.ServiceProvider.GetRequiredService<TenantDbContext>();
                db.MessageFailures.Add(new MessageFailure(tenantId, envelope.Id, envelope.MessageType ?? description, description, envelope.Attempts,
                    exception.GetType().Name, Redaction.Text(exception.Message, MessageFailure.ErrorMaxLength), envelope.CorrelationId,
                    runtime.Services.GetRequiredService<TimeProvider>().GetUtcNow()));
                await db.SaveChangesAsync();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                LogNotRecorded(logger, e, description, envelope.TenantId);
            }
        }
        await lifecycle.MoveToDeadLetterQueueAsync(exception);
    }

    [LoggerMessage(Level = LogLevel.Critical, Message = "Falha definitiva em segundo plano: {Description} (loja {TenantId}, {Attempts} tentativas)")]
    private static partial void LogFailure(ILogger logger, Exception exception, string description, string? tenantId, int attempts);

    [LoggerMessage(Level = LogLevel.Error, Message = "Não foi possível registrar a falha \"{Description}\" da loja {TenantId}")]
    private static partial void LogNotRecorded(ILogger logger, Exception exception, string description, string? tenantId);
}
