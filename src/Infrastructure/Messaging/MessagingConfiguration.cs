using JasperFx;
using Wolverine;
using Wolverine.EntityFrameworkCore;
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

    /// <param name="connectionString">Conexão do app_user (sem DDL).</param>
    /// <param name="buildStorage">true só para o papel de migração: cria/atualiza as tabelas do Wolverine.</param>
    public static WolverineOptions ConfigureMessaging(this WolverineOptions opts, string connectionString, bool buildStorage = false)
    {
        opts.PersistMessagesWithPostgresql(connectionString, Schema);
        opts.Discovery.IncludeAssembly(typeof(MessagingConfiguration).Assembly); // handlers de negócio (ex.: estoque)
        opts.UseEntityFrameworkCoreTransactions();
        // O código gerado pelo Wolverine instancia serviços "inline"; o contexto de tenant precisa ser a MESMA
        // instância no middleware, no handler e no DbContext — então vem sempre do contêiner do escopo da mensagem.
        opts.CodeGeneration.AlwaysUseServiceLocationFor<TenantScope>();
        opts.CodeGeneration.AlwaysUseServiceLocationFor<ITenantContext>();
        opts.CodeGeneration.AlwaysUseServiceLocationFor<TenantDbContext>();

        opts.Policies.AddMiddleware(typeof(TenantMessageMiddleware));
        opts.Policies.UseDurableLocalQueues();
        opts.Policies.AutoApplyTransactions();
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
