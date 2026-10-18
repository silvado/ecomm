using Ecommerce.Application.Ai;
using Ecommerce.Application.Channels;
using Ecommerce.Application.Integrations;
using Ecommerce.Domain.Channels;
using Ecommerce.Infrastructure.Channels.MercadoLivre;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Wolverine;

namespace Ecommerce.Infrastructure.Channels.Questions;

/// <summary>
/// Perguntas de compradores do ML (RF31), na transação do Wolverine:
/// <list type="bullet">
/// <item>Importar: consulta a pergunta no ML (o aviso nunca é confiado), registra a nova e pede o rascunho; pergunta já
/// registrada acompanha o ML (respondida lá, excluída, banida).</item>
/// <item>Rascunho: IA só com o cadastro (<see cref="BuyerAnswerAdvisor"/>) e as regras fixas (<see cref="BuyerAnswerRules"/>).
/// Simples com a resposta automática ligada vai para envio; o resto fica para a loja revisar. IA fora do ar não trava nada:
/// fica a nota e a loja responde à mão.</item>
/// <item>Enviar: uma resposta por pergunta (trava da linha), <c>POST /answers</c> no ML.</item>
/// </list>
/// </summary>
public static class BuyerQuestionHandler
{
    private const string Source = SalesChannel.MercadoLivre;

    public static async Task Handle(ImportBuyerQuestion command, TenantDbContext db, MercadoLivreApiClient api, MercadoLivreCredentials credentials,
        IMessageBus bus, TimeProvider clock, CancellationToken ct)
    {
        string token;
        try { token = await credentials.AccessTokenAsync(command.ConnectionId, ct); }
        catch (ChannelAuthorizationRevokedException) { return; } // a loja autoriza de novo; a pergunta volta no próximo aviso
        if (await api.GetQuestionAsync(token, command.QuestionId, ct) is not { } snapshot) return;

        var now = clock.GetUtcNow();
        var question = await db.BuyerQuestions.SingleOrDefaultAsync(q => q.ConnectionId == command.ConnectionId && q.ExternalId == snapshot.Id, ct);
        if (question is not null)
        {
            FollowChannel(question, snapshot, now);
            return;
        }
        // Só entra pergunta à espera de resposta (as já respondidas lá não interessam ao painel).
        if (snapshot.Status is not ("UNANSWERED" or "UNDER_REVIEW") || snapshot.ItemId.Length is 0 or > ChannelListing.ExternalIdMaxLength
            || snapshot.Text.Trim().Length == 0 || snapshot.Id.Length > BuyerQuestion.ExternalIdMaxLength)
            return;

        var partId = await db.ChannelListings.AsNoTracking()
            .Where(l => l.ConnectionId == command.ConnectionId && l.ExternalId == snapshot.ItemId)
            .Select(l => (Guid?)l.PartId).FirstOrDefaultAsync(ct);
        question = new BuyerQuestion(db.RequireTenantId(), command.ConnectionId, snapshot.Id, snapshot.ItemId, partId, snapshot.Text, snapshot.AskedAt ?? now, now);
        db.BuyerQuestions.Add(question);
        if (partId is null)
        {
            question.Drafted(null, null, "Anúncio que não foi publicado pela plataforma: responda com os dados do anúncio no Mercado Livre.", now);
            return;
        }
        await bus.PublishAsync(new DraftBuyerAnswer(question.Id), new DeliveryOptions { TenantId = db.RequireTenantId().ToString() });
    }

    private static void FollowChannel(BuyerQuestion question, ChannelQuestionSnapshot snapshot, DateTimeOffset now)
    {
        if (question.Status != BuyerQuestionStatus.Pending) return;
        switch (snapshot.Status)
        {
            case "ANSWERED":
                question.Answered(snapshot.AnswerText is { Length: > 0 } text ? text : "(respondida no Mercado Livre)", AnswerOrigin.Channel, null,
                    snapshot.AnsweredAt ?? now, now);
                break;
            case "BANNED" or "DELETED" or "CLOSED_UNANSWERED" or "DISABLED":
                question.Close(now);
                break;
        }
    }

    public static async Task<DraftOutcome> Handle(DraftBuyerAnswer command, TenantDbContext db, IAiCompletion ai, IMessageBus bus, TimeProvider clock,
        CancellationToken ct)
    {
        var question = await db.BuyerQuestions.SingleOrDefaultAsync(q => q.Id == command.QuestionId, ct);
        if (question is null || question.Status != BuyerQuestionStatus.Pending) return DraftOutcome.Skipped;
        var now = clock.GetUtcNow();
        if (question.PartId is not { } partId || await BuyerAnswerAdvisor.LoadAsync(db, partId, ct) is not { } facts)
        {
            question.Drafted(null, null, "A peça deste anúncio não está mais no cadastro: responda à mão.", now);
            return DraftOutcome.NoDraft;
        }

        AiResult result;
        try
        {
            result = await ai.CompleteAsync(BuyerAnswerAdvisor.Request(question, facts), ct);
        }
        catch (AiInvalidOutputException)
        {
            question.Drafted(null, null, "A IA não conseguiu sugerir uma resposta: responda à mão ou peça um novo rascunho.", now);
            return DraftOutcome.NoDraft;
        }
        catch (AiException e)
        {
            question.Drafted(null, null, $"{e.Message} Responda à mão ou peça um novo rascunho.", now);
            return DraftOutcome.NoDraft;
        }

        var auto = await db.StoreSettings.AsNoTracking().Select(s => s.AutoAnswerQuestions).FirstOrDefaultAsync(ct);
        var decision = BuyerAnswerRules.Decide(question.Text, BuyerAnswerAdvisor.Read(result.Output), facts.Compatibilities.Count > 0, auto, facts.Codes);
        question.Drafted(decision.Draft, decision.Kind, decision.Note, now);
        // Rascunho pedido de novo no painel nunca sai sozinho: quem pediu revisa e envia.
        if (!decision.SendNow || command.UserId is not null) return DraftOutcome.Drafted;
        await bus.PublishAsync(new SendBuyerAnswer(question.Id, decision.Draft!, null), new DeliveryOptions { TenantId = db.RequireTenantId().ToString() });
        return DraftOutcome.SentAutomatically;
    }

    public static async Task<AnswerResult> Handle(SendBuyerAnswer command, TenantDbContext db, MercadoLivreApiClient api, MercadoLivreCredentials credentials,
        IIntegrationLog log, TimeProvider clock, CancellationToken ct)
    {
        string text;
        try { text = BuyerQuestion.NormalizeAnswer(command.Text); }
        catch (ArgumentException e) { return new AnswerResult(AnswerOutcome.Invalid, e.Message.Split(" (Parameter", 2)[0]); }

        // Uma resposta por pergunta: o segundo envio simultâneo espera a trava e encontra a pergunta já respondida.
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM buyer_questions WHERE id = {command.QuestionId} FOR UPDATE").ToListAsync(ct);
        var question = await db.BuyerQuestions.SingleOrDefaultAsync(q => q.Id == command.QuestionId, ct);
        if (question is null) return new AnswerResult(AnswerOutcome.NotFound, "Pergunta não encontrada.");
        if (question.Status != BuyerQuestionStatus.Pending)
            return new AnswerResult(AnswerOutcome.Conflict, question.Status == BuyerQuestionStatus.Answered
                ? "Esta pergunta já foi respondida."
                : "Esta pergunta foi encerrada no Mercado Livre e não aceita mais resposta.");

        var now = clock.GetUtcNow();
        string token;
        try
        {
            token = await credentials.AccessTokenAsync(question.ConnectionId, ct);
        }
        catch (ChannelAuthorizationRevokedException)
        {
            const string message = "A conexão com o Mercado Livre precisa ser autorizada de novo (Canais de venda). A resposta não foi enviada.";
            question.SendFailed(message, now);
            return new AnswerResult(AnswerOutcome.Unavailable, message);
        }

        ChannelQuestionSnapshot answered;
        try
        {
            answered = await Integrations.Logged.RunAsync(log, Source, command.UserId is null ? "responder pergunta (automática)" : "responder pergunta",
                () => api.AnswerQuestionAsync(token, question.ExternalId, text, ct), q => $"pergunta {q.Id} · {q.Status}", null, question.PartId, ct);
        }
        catch (ChannelValidationException e)
        {
            var message = $"O Mercado Livre recusou a resposta: {e.Message}";
            question.SendFailed(message, now);
            return new AnswerResult(AnswerOutcome.Refused, message);
        }
        catch (ChannelConnectorException e) when (command.UserId is not null)
        {
            question.SendFailed(e.Message, now);
            return new AnswerResult(AnswerOutcome.Unavailable, e.Message);
        }
        // Automática com o ML fora do ar: a exceção sobe e a fila tenta de novo (RF28).

        question.Answered(answered.AnswerText ?? text, command.UserId is null ? AnswerOrigin.Automatic : AnswerOrigin.Panel, command.UserId,
            answered.AnsweredAt ?? now, now);
        return new AnswerResult(AnswerOutcome.Sent);
    }
}
