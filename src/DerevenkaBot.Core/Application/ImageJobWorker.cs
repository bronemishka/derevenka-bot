using System.Security.Cryptography;
using System.Text;
using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DerevenkaBot.Core.Application;

public sealed class ImageJobWorker(ImageDatabase db, ImageOptions options, IImageGenerationProvider provider,
    IImageAssetStore assets, ITelegramGateway telegram, ILogger<ImageJobWorker> logger) : BackgroundService
{
    private readonly ImageStatusService statuses = new(db, telegram, logger);
    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.WhenAll(
        Enumerable.Range(0, options.MaxConcurrentJobs).Select(_ => Loop(stoppingToken)));
    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { if (!await ProcessOneAsync(ct)) await Task.Delay(500, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogError("Image worker infrastructure error: {Type}", ex.GetType().Name);
                await Task.Delay(1000, ct);
            }
        }
    }
    public async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        var owner = Guid.NewGuid().ToString("N");
        var job = db.Run(s =>
        {
            var now = DateTimeOffset.UtcNow;
            var access = new ImageAccessService(db, options);
            foreach (var denied in s.Jobs.Where(x => x.Status is JobStatus.Queued or JobStatus.Ready && !access.IsAllowed(s, x.Actor)))
            {
                if (denied.Status == JobStatus.Queued) s.Usage.Single(x => x.Id == denied.Id).Released = true;
                denied.Status = JobStatus.Cancelled; denied.ErrorCode = "access_revoked";
            }
            foreach (var expired in s.Jobs.Where(x => x.LeaseUntil < now && x.Status is JobStatus.Generating or JobStatus.Publishing))
            {
                expired.Status = expired.Status == JobStatus.Generating ? JobStatus.Unknown : JobStatus.Ready;
                expired.ErrorCode = "lease_expired";
            }
            var running = s.Jobs.Where(x => x.Status is JobStatus.Generating or JobStatus.Publishing).ToList();
            if (running.Count >= options.MaxConcurrentJobs) return null;
            var next = s.Jobs.OrderBy(x => x.CreatedAt).FirstOrDefault(x =>
                x.NextAttemptAt <= now && x.Status is JobStatus.Queued or JobStatus.Ready &&
                running.Count(y => y.Actor.UserId == x.Actor.UserId) < options.MaxConcurrentJobsPerUser &&
                !running.Any(y => s.Generations.Single(g => g.Id == y.GenerationId).SessionId ==
                    s.Generations.Single(g => g.Id == x.GenerationId).SessionId));
            if (next == null) return null;
            next.Status = next.Status == JobStatus.Queued ? JobStatus.Generating : JobStatus.Publishing;
            next.LeaseOwner = owner; next.LeaseUntil = now.AddSeconds(options.LeaseSeconds);
            if (next.Status == JobStatus.Generating) next.Attempts++; else next.PublishAttempts++;
            return next;
        });
        if (job == null) return false;
        logger.LogInformation("Image job {JobId}: {Status}", job.Id, job.Status);
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var heartbeat = Heartbeat(job.Id, owner, heartbeatCts.Token);
        try
        {
            if (job.Status == JobStatus.Generating) await Generate(job, owner, ct);
            else await Publish(job, owner, ct);
        }
        finally
        {
            await heartbeatCts.CancelAsync();
            try { await heartbeat; } catch (OperationCanceledException) { }
        }
        return true;
    }
    private async Task Heartbeat(Guid id, string owner, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.LeaseSeconds / 3));
        while (await timer.WaitForNextTickAsync(ct))
            db.Run(s =>
            {
                var job = s.Jobs.Single(x => x.Id == id);
                if (job.LeaseOwner == owner && job.Status is JobStatus.Generating or JobStatus.Publishing)
                    job.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(options.LeaseSeconds);
                return true;
            });
    }
    private async Task Generate(ImageJob job, string owner, CancellationToken ct)
    {
        var providerStarted = false;
        try
        {
            var generation = db.Run(s => s.Generations.Single(x => x.Id == job.GenerationId));
            var inputs = new List<ProviderAsset>();
            foreach (var id in generation.InputAssets)
            {
                await using var stream = await assets.OpenReadAsync(id, ct);
                using var bytes = new MemoryStream();
                await stream.CopyToAsync(bytes, ct);
                inputs.Add(new(bytes.ToArray(), "image/png"));
            }
            var safety = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(options.SafetySalt),
                Encoding.UTF8.GetBytes(job.Actor.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture))));
            var request = new ImageProviderRequest(generation.Model, generation.Prompt, inputs, generation.Quality,
                generation.Size, options.Complexity, safety, job.Id);
            if (!new ImageAccessService(db, options).IsAllowed(job.Actor)) throw new ProviderException("access_revoked");
            providerStarted = true;
            var result = await GenerateWithActivity(request, job, ct);
            using var data = new MemoryStream(result.Bytes, writable: false);
            var asset = await assets.SaveAsync(data, result.ContentType, ct);
            var saved = db.Run(s =>
            {
                var current = s.Jobs.Single(x => x.Id == job.Id);
                if (current.LeaseOwner != owner || current.Status != JobStatus.Generating) return false;
                var node = s.Generations.Single(x => x.Id == job.GenerationId);
                var assetIndex = s.Assets.FindIndex(x => x.Id == asset.Id);
                s.Assets[assetIndex] = asset with { Owner = job.Actor };
                node.ResultAssetId = asset.Id;
                var session = s.Sessions.Single(x => x.Id == node.SessionId);
                if (!session.Closed && session.Version == job.SessionVersion &&
                    s.Active.Any(x => x.Id == DatabaseState.ActorKey(job.Actor) && x.SessionId == session.Id))
                { session.Head = node.Id; session.Version++; }
                var usage = s.Usage.Single(x => x.Id == job.Id);
                usage.ActualUsd = result.ActualUsd; usage.RawUsage = result.UsageJson; usage.ProviderRequestId = result.RequestId;
                current.Status = JobStatus.Ready; current.LeaseOwner = null; current.ErrorCode = null;
                return true;
            });
            if (saved) await Notify(job, Texts.Publishing, ct);
        }
        catch (Exception ex)
        {
            var error = ex as ProviderException;
            var uncertain = error?.Uncertain ?? providerStarted;
            var retry = error?.Retryable == true && !uncertain && job.Attempts < options.MaxAttempts;
            db.Run(s =>
            {
                var current = s.Jobs.Single(x => x.Id == job.Id);
                if (current.LeaseOwner != owner) return false;
                current.ErrorCode = error?.Code ?? "local_error";
                current.Status = uncertain ? JobStatus.Unknown : retry ? JobStatus.Queued : JobStatus.Failed;
                current.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, job.Attempts) + Random.Shared.NextDouble() * 3);
                if (!uncertain && !retry) s.Usage.Single(x => x.Id == job.Id).Released = true;
                return true;
            });
            if (!retry) await Notify(job, uncertain ? Texts.Unknown : Texts.Failure, ct);
        }
    }
    private async Task Publish(ImageJob job, string owner, CancellationToken ct)
    {
        try
        {
            var asset = db.Run(s => s.Assets.Single(x => x.Id == s.Generations.Single(g => g.Id == job.GenerationId).ResultAssetId));
            await using var stream = await assets.OpenReadAsync(asset.Id, ct);
            var messageId = await telegram.PublishAsync(job, asset, stream, ct);
            var published = db.Run(s =>
            {
                var current = s.Jobs.Single(x => x.Id == job.Id);
                if (current.LeaseOwner != owner) return false;
                s.Messages.Add(new($"{job.Actor.ChatId}:{messageId}", job.Actor.ChatId, messageId,
                    job.GenerationId, asset.Id, !job.Actor.IsPrivate));
                current.Status = JobStatus.Completed; current.LeaseOwner = null;
                return true;
            });
            if (published) await Notify(job, Texts.Delivered, ct);
        }
        catch (Exception)
        {
            db.Run(s =>
            {
                var current = s.Jobs.Single(x => x.Id == job.Id);
                if (current.LeaseOwner != owner) return false;
                // Keep the completed asset even if Telegram delivery exhausts its attempts.
                current.Status = job.PublishAttempts < options.MaxAttempts ? JobStatus.Ready : JobStatus.Failed;
                current.ErrorCode = "telegram_delivery";
                current.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Pow(2, job.PublishAttempts) + Random.Shared.NextDouble() * 3);
                return true;
            });
        }
    }
    private async Task Notify(ImageJob job, string text, CancellationToken ct)
    {
        await statuses.NotifyAsync(job.Id, text, ct);
    }
    private async Task<ImageProviderResult> GenerateWithActivity(ImageProviderRequest request, ImageJob job, CancellationToken ct)
    {
        using var activityCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var activity = ShowActivity(job, activityCts.Token);
        try { return request.InputAssets.Count == 0 ? await provider.GenerateAsync(request, ct) : await provider.EditAsync(request, ct); }
        finally
        {
            await activityCts.CancelAsync();
            try { await activity; } catch (OperationCanceledException) { }
        }
    }
    private async Task ShowActivity(ImageJob job, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var current = db.Run(s => s.Jobs.Single(x => x.Id == job.Id));
                var mode = await telegram.ShowGenerationProgressAsync(current, ct);
                await statuses.GeneratingAsync(job.Id, mode, ct);
            }
            catch (Exception) { if (ct.IsCancellationRequested) return; }
            await Task.Delay(TimeSpan.FromSeconds(4), ct);
        }
    }
}
