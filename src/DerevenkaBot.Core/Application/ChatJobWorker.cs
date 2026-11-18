using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DerevenkaBot.Core.Application;

public sealed class ChatJobWorker(ImageDatabase db, ImageOptions images, ChatOptions options, ChatService chats,
    ILogger<ChatJobWorker> logger) : BackgroundService
{
    private readonly ImageAccessService access = new(db, images);
    private TimeSpan LeaseDuration => TimeSpan.FromSeconds(options.TimeoutSeconds + 30);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Enumerable.Range(0, options.MaxConcurrentRequests).Select(_ => Loop(stoppingToken)));

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { if (!await ProcessOneAsync(ct)) await Task.Delay(500, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning("Chat worker infrastructure error: {Type}", ex.GetType().Name);
                await Task.Delay(1000, ct);
            }
        }
    }

    public async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var owner = Guid.NewGuid().ToString("N");
        var request = db.Run(s =>
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var item in s.ChatRequests.Where(x => x.Status is JobStatus.Queued or JobStatus.Generating))
            {
                var chat = s.Chats.Find(x => x.Id == DatabaseState.ActorKey(item.Actor));
                if (!images.Enabled || !options.Enabled || !access.IsAllowed(s, item.Actor) || chat?.Version != item.ConversationVersion)
                { item.Status = JobStatus.Cancelled; item.LeaseOwner = null; continue; }
                if (item.Status == JobStatus.Generating && item.LeaseUntil <= now)
                {
                    RecoverAnswer(s, item);
                    item.Status = JobStatus.Queued; item.LeaseOwner = null;
                }
            }
            var running = s.ChatRequests.Where(x => x.Status == JobStatus.Generating).ToArray();
            if (running.Length >= options.MaxConcurrentRequests) return null;
            var next = s.ChatRequests.OrderBy(x => x.Sequence).FirstOrDefault(x =>
                x.Status == JobStatus.Queued && x.NextAttemptAt <= now && !running.Any(y => y.Actor == x.Actor) &&
                !s.ChatRequests.Any(y => y.Actor == x.Actor && y.Status == JobStatus.Queued && y.Sequence < x.Sequence));
            if (next == null) return null;
            next.Status = JobStatus.Generating; next.LeaseOwner = owner; next.LeaseUntil = now.Add(LeaseDuration);
            return next;
        });
        if (request == null) return false;
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Heartbeat(request.Id, owner, heartbeatCts.Token);
        try
        {
            await chats.ReplyAsync(request.Actor, request.MessageId, request.Prompt, request.Id, ct, request);
            db.Run(s =>
            {
                var current = s.ChatRequests.Single(x => x.Id == request.Id);
                if (current.LeaseOwner == owner && current.Status == JobStatus.Generating)
                {
                    current.Status = access.IsAllowed(s, current.Actor) && options.Enabled &&
                        s.Chats.Any(x => x.Id == DatabaseState.ActorKey(current.Actor) && x.Version == current.ConversationVersion)
                        ? JobStatus.Completed : JobStatus.Cancelled;
                    current.LeaseOwner = null;
                }
                return true;
            });
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Leave the lease for recovery. Never repeat a possibly paid in-flight request.
            throw;
        }
        catch (Exception ex)
        {
            db.Run(s =>
            {
                var current = s.ChatRequests.Single(x => x.Id == request.Id);
                if (current.LeaseOwner != owner || current.Status != JobStatus.Generating) return false;
                RecoverAnswer(s, current);
                current.Status = JobStatus.Queued; current.LeaseOwner = null; current.DeliveryFailures++;
                current.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(60, Math.Pow(2, Math.Min(6, current.DeliveryFailures))));
                return true;
            });
            logger.LogWarning("Chat delivery deferred: {Type}", ex.GetType().Name);
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try { await heartbeat; } catch (OperationCanceledException) when (heartbeatCts.IsCancellationRequested) { }
        }
        return true;
    }

    private void RecoverAnswer(DatabaseState state, ChatRequest request)
    {
        var chat = state.Chats.Find(x => x.Id == DatabaseState.ActorKey(request.Actor));
        if (chat == null || chat.Version != request.ConversationVersion || chat.Turns.Any(x => x.Key == request.Id) ||
            !state.Updates.Any(x => x.Id == request.Id)) return;
        chat.Turns.Add(new() { Key = request.Id, Prompt = request.Prompt, Answer = Texts.ChatInterrupted, Failed = true });
        chat.Turns = chat.Turns.TakeLast(options.HistoryTurns).ToList();
    }

    private async Task Heartbeat(string id, string owner, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(ct))
            db.Run(s =>
            {
                var request = s.ChatRequests.Single(x => x.Id == id);
                if (request.LeaseOwner == owner && request.Status == JobStatus.Generating)
                    request.LeaseUntil = DateTimeOffset.UtcNow.Add(LeaseDuration);
                return true;
            });
    }
}
