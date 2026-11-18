using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DerevenkaBot.Core.Application;

// A job has one visible progress surface: either its permanent status message or a native AI draft.
public sealed class ImageStatusService(ImageDatabase db, ITelegramGateway telegram, ILogger? logger = null)
{
    // Bounded process-wide gates also coordinate the handler's late acknowledgement with the worker.
    private static readonly SemaphoreSlim[] Gates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    public async Task AcknowledgeAsync(Guid jobId, int messageId, CancellationToken ct)
    {
        await Try(async () =>
        {
            var current = db.Run(s =>
            {
                var job = s.Jobs.Single(x => x.Id == jobId);
                job.StatusAcknowledged = true;
                job.StatusMessageId ??= messageId;
                return job;
            });
            // A terminal error may already have posted a status while the initial acknowledgement was in flight.
            if (current.StatusMessageId != messageId)
                await telegram.DeleteStatusAsync(current.Actor.ChatId, messageId, ct);
            if (current.NativeProgress) await HideAsync(current, ct);
            else if (current.Status is not (JobStatus.Queued or JobStatus.Generating))
                await UpdateAsync(current.Id, current.Status switch {
                    JobStatus.Generating => Texts.Generating,
                    JobStatus.Ready or JobStatus.Publishing => Texts.Publishing,
                    JobStatus.Completed => Texts.Delivered,
                    JobStatus.Unknown => Texts.Unknown,
                    _ => Texts.Failure }, true, ct);
        }, jobId, ct);
    }
    public Task GeneratingAsync(Guid jobId, GenerationProgressMode mode, CancellationToken ct) => Try(async () =>
    {
        var current = db.Run(s =>
        {
            var job = s.Jobs.Single(x => x.Id == jobId);
            job.NativeProgress = mode == GenerationProgressMode.NativeDraft;
            return job;
        });
        if (current.NativeProgress) await HideAsync(current, ct);
        else if (current.StatusAcknowledged) await UpdateAsync(jobId, Texts.Generating, true, ct);
    }, jobId, ct);
    public Task NotifyAsync(Guid jobId, string text, CancellationToken ct) => Try(async () =>
    {
        var job = db.Run(s => s.Jobs.Single(x => x.Id == jobId));
        if (job.NativeProgress && (text == Texts.Publishing || text == Texts.Delivered))
        { await HideAsync(job, ct); return; }
        if (text == Texts.Failure || text == Texts.Unknown)
            db.Run(s => { s.Jobs.Single(x => x.Id == jobId).NativeProgress = false; return true; });
        await UpdateAsync(jobId, text, text == Texts.Failure || text == Texts.Unknown, ct);
    }, jobId, ct);
    private async Task HideAsync(ImageJob job, CancellationToken ct)
    {
        if (job.StatusMessageId is not { } id) return;
        await telegram.DeleteStatusAsync(job.Actor.ChatId, id, ct);
        db.Run(s =>
        {
            var current = s.Jobs.Single(x => x.Id == job.Id);
            if (current.StatusMessageId == id) { current.StatusMessageId = null; current.LastStatusText = null; }
            return true;
        });
    }
    private async Task UpdateAsync(Guid jobId, string text, bool allowCreate, CancellationToken ct)
    {
        var job = db.Run(s => s.Jobs.Single(x => x.Id == jobId));
        if (job.StatusMessageId != null && job.LastStatusText == text) return;
        var id = job.StatusMessageId;
        if (id != null) await telegram.EditStatusAsync(job.Actor.ChatId, id.Value, text, ct);
        else if (allowCreate) id = await telegram.SendTextAsync(job.Actor.ChatId, job.MessageId, text, ct);
        else return;
        db.Run(s =>
        {
            var current = s.Jobs.Single(x => x.Id == jobId);
            current.StatusMessageId = id; current.LastStatusText = text;
            return true;
        });
    }
    private async Task Try(Func<Task> action, Guid jobId, CancellationToken ct)
    {
        var gate = Gates[(jobId.GetHashCode() & int.MaxValue) % Gates.Length];
        var entered = false;
        try { await gate.WaitAsync(ct); entered = true; await action(); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) { logger?.LogWarning("Cannot update presentation for job {JobId}", jobId); }
        finally { if (entered) gate.Release(); }
    }
}
