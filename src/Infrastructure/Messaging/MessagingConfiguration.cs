using JasperFx;
using Ecommerce.Application.Email;
using Ecommerce.Application.Integrations;
using Ecommerce.Infrastructure.Email;
using Ecommerce.Application.Shipping;
using Ecommerce.Infrastructure.Integrations;
using Npgsql;
using Wolverine;
using Wolverine.EntityFrameworkCore;
using Wolverine.ErrorHandling;
using Ecommerce.Application.Payments;
using Ecommerce.Application.Tenancy;
using Ecommerce.Infrastructure.Persistence;
using Ecommerce.Infrastructure.Tenancy;
using Wolverine.Postgresql;

namespace Ecommerce.Infrastructure.Messaging;

/// <summary>
/// Configuração do Wolverine (ADR-0002): outbox/inbox no PostgreSQL, filas locais duráveis e escopo de tenant por mensagem.
/// </summary>
public static class MessagingConfiguration
{
    public const string Schema = "wolverine";

    /// <summary>ADR-0002 / RF28 CA1: novas tentativas de mensagem com falha transitória.</summary>
    public static readonly TimeSpan[] DefaultRetryDelays =
        [TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(10), TimeSpan.FromMinutes(30)];

    /// <param name="connectionString">Conexão do app_user (sem DDL).</param>
    /// <param name="buildStorage">true só para o papel de migração: cria/atualiza as tabelas do Wolverine.</param>
    /// <param name="retryDelays">Intervalos das novas tentativas (padrão <see cref="DefaultRetryDelays"/>; testes usam menores).</param>
    public static WolverineOptions ConfigureMessaging(this WolverineOptions opts, string connectionString, bool buildStorage = false,
        TimeSpan[]? retryDelays = null)
    {
        opts.PersistMessagesWithPostgresql(connectionString, Schema);
        opts.Discovery.IncludeAssembly(typeof(MessagingConfiguration).Assembly); // handlers de negócio (ex.: estoque)
        opts.UseEntityFrameworkCoreTransactions();
        // O código gerado pelo Wolverine instancia serviços "inline"; o contexto de tenant precisa ser a MESMA
        // instância no middleware, no handler e no DbContext — então vem sempre do contêiner do escopo da mensagem.
        opts.CodeGeneration.AlwaysUseServiceLocationFor<TenantScope>();
        opts.CodeGeneration.AlwaysUseServiceLocationFor<ITenantContext>();
        opts.CodeGeneration.AlwaysUseServiceLocationFor<TenantDbContext>();
        opts.CodeGeneration.AlwaysUseServiceLocationFor<IPaymentGatewayResolver>(); // depende do tenant e do cofre
        opts.CodeGeneration.AlwaysUseServiceLocationFor<IIntegrationLog>(); // grava na loja do escopo
        opts.CodeGeneration.AlwaysUseServiceLocationFor<IEmailSender>(); // servidor e cofre da loja do escopo
        opts.CodeGeneration.AlwaysUseServiceLocationFor<EmailBranding>();

        opts.Policies.AddMiddleware(typeof(TenantMessageMiddleware));
        opts.Policies.UseDurableLocalQueues();
        opts.Policies.AutoApplyTransactions();

        // RF28: falha transitória (serviço externo fora do ar, tempo esgotado, banco indisponível) tenta de novo com
        // intervalos crescentes e variação aleatória (jitter, para não chegar todo mundo junto); esgotadas as tentativas, ou
        // em erro que não é transitório, a falha é registrada para a loja e a mensagem vai para a fila de falhas.
        // Só vale para mensagens da fila: em chamadas diretas (InvokeAsync) o erro volta na hora para quem chamou.
        opts.OnException<HttpRequestException>()
            .Or<TimeoutException>()
            .Or<PaymentGatewayException>()
            .Or<ShippingProviderException>()
            .Or<EmailDeliveryException>()
            .Or<NpgsqlException>(e => e.IsTransient, "PostgreSQL transitório")
            .ScheduleRetry(retryDelays ?? DefaultRetryDelays).WithBoundedJitter(0.2)
            .Then.CustomAction(MessageFailures.RecordAndDeadLetterAsync, "registrar a falha para a loja");
        opts.OnAnyException().CustomAction(MessageFailures.RecordAndDeadLetterAsync, "registrar a falha para a loja");
        opts.AutoBuildMessageStorageOnStartup = buildStorage ? AutoCreate.CreateOrUpdate : AutoCreate.None;
        return opts;
    }

    /// <summary>Permissões do app_user nas tabelas do Wolverine. Executar como superusuário após criar o schema.</summary>
    public const string GrantPrivileges = $"""
        GRANT USAGE ON SCHEMA {Schema} TO app_user;
        GRANT SELECT, INSERT, UPDATE, DELETE ON ALL TABLES IN SCHEMA {Schema} TO app_user;
        GRANT USAGE, SELECT ON ALL SEQUENCES IN SCHEMA {Schema} TO app_user;
        GRANT EXECUTE ON ALL FUNCTIONS IN SCHEMA {Schema} TO app_user;
        """;
}
