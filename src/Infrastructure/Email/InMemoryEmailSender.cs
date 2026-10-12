using System.Collections.Concurrent;
using Ecommerce.Application.Email;
using Ecommerce.Application.Integrations;
using Ecommerce.Application.Tenancy;
using Ecommerce.Infrastructure.Integrations;

namespace Ecommerce.Infrastructure.Email;

/// <summary>E-mails "enviados" nos testes, com a loja de origem.</summary>
public sealed class InMemoryEmailOutbox
{
    private readonly ConcurrentQueue<SentEmail> sent = new();

    public IReadOnlyList<SentEmail> Sent => [.. sent];

    private readonly Lock gate = new();
    private int failNext;

    /// <summary>Quantos dos próximos envios falham como servidor fora do ar (testes de novas tentativas).</summary>
    public void FailNext(int count)
    {
        lock (gate) failNext = count;
    }

    internal bool ShouldFail()
    {
        lock (gate)
        {
            if (failNext <= 0) return false;
            failNext--;
            return true;
        }
    }

    internal void Add(SentEmail email) => sent.Enqueue(email);

    public void Clear() => sent.Clear();
}

public sealed record SentEmail(Guid? TenantId, EmailMessage Message, string Purpose, Guid? OrderId);

/// <summary>Implementação falsa de <see cref="IEmailSender"/> (testes): guarda a mensagem e registra no log de integração como o SMTP.</summary>
public sealed class InMemoryEmailSender(InMemoryEmailOutbox outbox, ITenantContext tenant, IIntegrationLog log) : IEmailSender
{
    public Task SendAsync(EmailMessage message, string purpose, Guid? orderId = null, CancellationToken ct = default) =>
        Logged.RunAsync(log, "email-teste", $"enviar e-mail ({purpose})", () =>
        {
            if (outbox.ShouldFail()) throw new EmailDeliveryException("Servidor de e-mail de teste fora do ar.");
            outbox.Add(new SentEmail(tenant.TenantId, message, purpose, orderId));
            return Task.FromResult(true);
        }, _ => null, orderId, null, ct);
}
