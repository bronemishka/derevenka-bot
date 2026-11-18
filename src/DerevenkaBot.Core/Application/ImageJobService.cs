using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Infrastructure;

namespace DerevenkaBot.Core.Application;

public sealed class ImageJobService(ImageDatabase db, ImageOptions options) : IImageJobService
{
    private readonly ImageAccessService access = new(db, options);
    public Task<EnqueueResult> EnqueueAsync(ImageCommand command, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(db.Run(s => Enqueue(s, command)));
    }
    private EnqueueResult Enqueue(DatabaseState s, ImageCommand c)
    {
        if (!access.IsAllowed(s, c.Actor)) return new(false, Texts.Denied);
        var duplicate = s.Jobs.Find(x => x.IdempotencyKey == c.Key);
        if (duplicate != null) return new(false, Texts.Duplicate, duplicate.Id);
        if (string.IsNullOrWhiteSpace(c.Prompt)) return new(false, Texts.MissingPrompt);
        if (c.Prompt.Length > 8000) return new(false, "Описание слишком длинное: максимум 8000 символов.");
        var now = DateTimeOffset.UtcNow;
        var active = s.Active.Find(x => x.Id == DatabaseState.ActorKey(c.Actor));
        var session = s.Sessions.Find(x => x.Id == active?.SessionId && !x.Closed);
        ImageGeneration? source = null;
        if (c.ReplyMessageId is { } reply)
        {
            var message = s.Messages.Find(x => x.ChatId == c.Actor.ChatId && x.MessageId == reply);
            if (message != null)
            {
                source = s.Generations.Find(x => x.Id == message.GenerationId);
                var owner = s.Sessions.Find(x => x.Id == source?.SessionId)?.Owner;
                if (owner == null || (owner.UserId != c.Actor.UserId && (!message.IsPublic || c.Actor.IsPrivate)))
                    return new(false, Texts.Unavailable);
            }
        }
        var input = c.InputAssetId;
        if (input != null && !s.Assets.Any(x => x.Id == input && x.Owner == c.Actor))
            return new(false, Texts.Unavailable);
        if (input == null && source != null) input = source.ResultAssetId;
        var newSession = c.StartNew || source != null || input != null || session == null;
        var parent = !newSession ? session?.Head : null;
        if (input == null && !c.StartNew && parent != null)
            input = s.Generations.Find(x => x.Id == parent)?.ResultAssetId;
        var draft = s.Drafts.Find(x => x.Id == DatabaseState.ActorKey(c.Actor) && x.ExpiresAt > now);
        if (input == null && !c.StartNew && draft != null) { input = draft.AssetId; newSession = true; parent = null; }
        if (c.RequireImage && input == null) return new(false, Texts.MissingImage);
        if (input is { } asset && !s.Assets.Any(x => x.Id == asset && x.ExpiresAt > now)) return new(false, Texts.Unavailable);
        if (options.LimitsEnabled && s.Jobs.Count(x => x.Status is JobStatus.Queued or JobStatus.Generating or JobStatus.Ready or JobStatus.Publishing) >= options.MaxPendingJobs)
            return new(false, Texts.QueueFull);
        // Pending continuation has no immutable result yet. Reject it rather than silently edit an older head.
        if (!c.StartNew && source == null && c.InputAssetId == null && s.Jobs.Any(x => x.Actor == c.Actor &&
            x.Status is JobStatus.Queued or JobStatus.Generating or JobStatus.Ready or JobStatus.Publishing)) return new(false, Texts.Busy);
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.CounterTimeZone);
        var localNow = TimeZoneInfo.ConvertTime(now, zone);
        var day = s.Usage.Where(x => !x.Released && TimeZoneInfo.ConvertTime(x.CreatedAt, zone).Date == localNow.Date).ToList();
        var month = s.Usage.Where(x => !x.Released && TimeZoneInfo.ConvertTime(x.CreatedAt, zone).ToString("yyyy-MM") == localNow.ToString("yyyy-MM")).ToList();
        var user = day.Where(x => x.UserId == c.Actor.UserId).ToList();
        decimal Cost(UsageRecord x) => x.ActualUsd ?? x.ReservedUsd;
        if (options.LimitsEnabled && (user.Count >= options.DailyJobsPerUser || day.Count >= options.GlobalDailyJobs || month.Count >= options.GlobalMonthlyJobs ||
            user.Sum(Cost) + options.ReservationUsd > options.UserDailyBudgetUsd ||
            day.Sum(Cost) + options.ReservationUsd > options.BotDailyBudgetUsd || month.Sum(Cost) + options.ReservationUsd > options.BotMonthlyBudgetUsd))
        {
            var next = month.Count >= options.GlobalMonthlyJobs || month.Sum(Cost) + options.ReservationUsd > options.BotMonthlyBudgetUsd
                ? new DateTime(localNow.Year, localNow.Month, 1).AddMonths(1) : localNow.Date.AddDays(1);
            return new(false, $"Лимит исчерпан. Следующий сброс: {next:dd.MM HH:mm} ({options.CounterTimeZone}). /usage — расходы.");
        }
        if (newSession)
        {
            session = new ImageSession { Owner = c.Actor };
            s.Sessions.Add(session);
            if (active == null) { active = new ActiveSession { Id = DatabaseState.ActorKey(c.Actor) }; s.Active.Add(active); }
            active.SessionId = session.Id;
        }
        var generation = new ImageGeneration
        {
            SessionId = session!.Id, ParentGenerationId = parent, SourceGenerationId = source?.Id,
            Prompt = c.Prompt, InputAssets = input is { } id ? [id] : [],
            Model = options.DefaultModel, Quality = options.DefaultQuality, Size = options.DefaultSize
        };
        var job = new ImageJob { IdempotencyKey = c.Key, GenerationId = generation.Id, Actor = c.Actor,
            MessageId = c.MessageId, SessionVersion = session.Version, NextAttemptAt = now };
        s.Generations.Add(generation); s.Jobs.Add(job);
        s.Usage.Add(new UsageRecord { Id = job.Id, UserId = c.Actor.UserId, ReservedUsd = options.ReservationUsd, PricingVersion = options.PricingVersion });
        if (draft != null) s.Drafts[s.Drafts.IndexOf(draft)] = draft with { ExpiresAt = now };
        return new(true, Texts.Queued, job.Id);
    }
    public bool Reset(Actor actor, string updateKey) => db.Run(s =>
    {
        if (!access.IsAllowed(s, actor) || s.Updates.Any(x => x.Id == updateKey)) return false;
        s.Updates.Add(new(updateKey));
        var active = s.Active.Find(x => x.Id == DatabaseState.ActorKey(actor));
        var session = s.Sessions.Find(x => x.Id == active?.SessionId);
        if (session != null)
        {
            session.Closed = true; session.Version++;
            foreach (var job in s.Jobs.Where(x => x.Actor == actor && x.Status == JobStatus.Queued &&
                         s.Generations.Any(g => g.Id == x.GenerationId && g.SessionId == session.Id)))
            { job.Status = JobStatus.Cancelled; s.Usage.Single(x => x.Id == job.Id).Released = true; }
        }
        if (active != null) active.SessionId = null;
        var draft = s.Drafts.Find(x => x.Id == DatabaseState.ActorKey(actor));
        if (draft != null) s.Drafts[s.Drafts.IndexOf(draft)] = draft with { ExpiresAt = DateTimeOffset.UtcNow };
        for (var i = 0; i < s.Prompts.Count; i++)
            if (s.Prompts[i].Owner == actor) s.Prompts[i] = s.Prompts[i] with { ExpiresAt = DateTimeOffset.UtcNow };
        return true;
    });
    public string Usage(Actor actor) => db.Run(s =>
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(options.CounterTimeZone);
        var day = TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, zone).Date;
        var rows = s.Usage.Where(x => x.UserId == actor.UserId && !x.Released && TimeZoneInfo.ConvertTime(x.CreatedAt, zone).Date == day).ToList();
        var count = options.LimitsEnabled ? $"{rows.Count}/{options.DailyJobsPerUser}" : rows.Count.ToString();
        var budget = options.LimitsEnabled ? $"Дневной бюджет: <b>${options.UserDailyBudgetUsd:F2}</b>." : "♾ Лимиты количества запросов, бюджета и длины очереди отключены.";
        var active = s.Jobs.Count(x => x.Actor.UserId == actor.UserId && x.Status is JobStatus.Queued or JobStatus.Generating or JobStatus.Ready or JobStatus.Publishing);
        return $"""
            <h2>📊 Твоя статистика</h2>
            <p>Сегодня · {Texts.Escape(options.CounterTimeZone)}</p>
            <table><tr><td>🎨 Заявок</td><td><b>{count}</b></td></tr>
            <tr><td>⏳ В работе и очереди</td><td><b>{active}</b></td></tr>
            <tr><td>💵 Оценка с резервами</td><td><b>${rows.Sum(x => x.ActualUsd ?? x.ReservedUsd):F4}</b></td></tr></table>
            <p>{budget}</p>
            <details><summary>Как считаются расходы?</summary><p>Это локальная оценка, не счёт OpenAI. Пока нет подтверждённого usage, учитываю оценочную стоимость. Запросы с неизвестным исходом сохраняют резерв.</p></details>
            <footer>Тариф: {Texts.Escape(options.PricingVersion)} · статистика учитывает твои запросы во всех чатах.</footer>
            """;
    });
}
