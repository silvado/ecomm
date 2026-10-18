using Ecommerce.Application.Channels;
using Ecommerce.Domain.Channels;
using Ecommerce.Domain.Inventory;
using Ecommerce.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Ecommerce.Infrastructure.Channels.Questions;

/// <summary>Perguntas de compradores no painel (RF31): mais antigas primeiro entre as pendentes, mais novas primeiro no histórico.</summary>
public sealed class BuyerQuestionService(TenantDbContext db) : IBuyerQuestions
{
    public const int MaxListed = 200;

    // Inicialização por membros (não construtor): o EF consegue ordenar pelos campos depois da projeção.
    private sealed class Row
    {
        public BuyerQuestion Question { get; init; } = null!;
        public string Channel { get; init; } = string.Empty;
        public string? PartTitle { get; init; }
    }

    public async Task<IReadOnlyList<BuyerQuestionView>> ListAsync(bool pendingOnly, CancellationToken ct = default)
    {
        var rows = Rows(pendingOnly ? q => q.Status == BuyerQuestionStatus.Pending : _ => true);
        rows = pendingOnly ? rows.OrderBy(r => r.Question.AskedAt) : rows.OrderByDescending(r => r.Question.AskedAt);
        return (await rows.Take(MaxListed).ToListAsync(ct)).Select(View).ToList();
    }

    public async Task<BuyerQuestionView?> GetAsync(Guid id, CancellationToken ct = default) =>
        await Rows(q => q.Id == id).FirstOrDefaultAsync(ct) is { } row ? View(row) : null;

    public async Task<QuestionSettingsView> SettingsAsync(CancellationToken ct = default) =>
        new(await db.StoreSettings.AsNoTracking().Select(s => s.AutoAnswerQuestions).FirstOrDefaultAsync(ct));

    public async Task<QuestionSettingsView> SetAutoAnswerAsync(bool enabled, CancellationToken ct = default)
    {
        var settings = await db.StoreSettings.FirstOrDefaultAsync(ct);
        if (settings is null)
        {
            settings = new StoreSettings(db.RequireTenantId());
            db.StoreSettings.Add(settings);
        }
        settings.SetAutoAnswerQuestions(enabled);
        await db.SaveChangesAsync(ct);
        return new QuestionSettingsView(settings.AutoAnswerQuestions);
    }

    private IQueryable<Row> Rows(System.Linq.Expressions.Expression<Func<BuyerQuestion, bool>> filter) =>
        from q in db.BuyerQuestions.AsNoTracking().Where(filter)
        join c in db.ChannelConnections.AsNoTracking() on q.ConnectionId equals c.Id
        join p in db.Parts.AsNoTracking() on q.PartId equals p.Id into parts
        from p in parts.DefaultIfEmpty()
        select new Row { Question = q, Channel = c.Channel, PartTitle = p == null ? null : p.Title };

    private static BuyerQuestionView View(Row row)
    {
        var q = row.Question;
        return new BuyerQuestionView(q.Id, row.Channel, q.ExternalItemId, q.PartId, row.PartTitle, q.Text, q.AskedAt, q.Status, q.DraftText, q.Kind,
            q.DraftNote, q.AnswerText, q.AnsweredVia, q.AnsweredAt, q.LastError);
    }
}
