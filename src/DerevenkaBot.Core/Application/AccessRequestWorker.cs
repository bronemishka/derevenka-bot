using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DerevenkaBot.Core.Application;

public sealed class AccessRequestWorker(ImageDatabase db, ImageOptions options, ITelegramGateway telegram,
    ILogger<AccessRequestWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { if (!await ProcessOneAsync(ct)) await Task.Delay(1500, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                logger.LogWarning("Access request notification failed: {Type}", ex.GetType().Name);
                await Task.Delay(3000, ct);
            }
        }
    }
    public async Task<bool> ProcessOneAsync(CancellationToken ct)
    {
        if (options.OwnerUserId <= 0) return false;
        var lease = Guid.NewGuid().ToString("N");
        var request = db.Run(s =>
        {
            var now = DateTimeOffset.UtcNow;
            var next = s.AccessRequests.OrderBy(x => x.CreatedAt).FirstOrDefault(x => x.NextAttemptAt <= now && x.LeaseUntil <= now &&
                (x.Status == AccessRequestStatus.Pending ? !x.OwnerNotified || x.NotificationOwnerId != options.OwnerUserId : !x.ApplicantNotified));
            if (next == null) return null;
            next.LeaseOwner = lease; next.LeaseUntil = now.AddMinutes(2); next.Attempts++;
            return next;
        });
        if (request == null) return false;
        var pending = request.Status == AccessRequestStatus.Pending;
        try
        {
            int? messageId = null;
            if (pending) messageId = await telegram.SendAccessReviewAsync(options.OwnerUserId, request, ct);
            else await telegram.SendAccessDecisionAsync(request, ct);
            db.Run(s =>
            {
                var current = s.AccessRequests.Single(x => x.Id == request.Id);
                if (current.LeaseOwner != lease) return false;
                if (pending)
                { current.OwnerNotified = true; current.OwnerMessageId = messageId; current.NotificationOwnerId = options.OwnerUserId; }
                else current.ApplicantNotified = true;
                current.LeaseOwner = null; current.LeaseUntil = DateTimeOffset.MinValue;
                current.NextAttemptAt = DateTimeOffset.UtcNow; current.Attempts = 0;
                return true;
            });
            if (!pending && request.OwnerMessageId is { } reviewId && request.NotificationOwnerId is { } ownerId)
            {
                try { await telegram.UpdateAccessReviewAsync(ownerId, reviewId, request, ct); }
                catch (Exception) { logger.LogWarning("Cannot update access review {RequestId}", request.Id); }
            }
        }
        catch (Exception ex)
        {
            db.Run(s =>
            {
                var current = s.AccessRequests.Single(x => x.Id == request.Id);
                if (current.LeaseOwner == lease)
                {
                    current.LeaseOwner = null; current.LeaseUntil = DateTimeOffset.MinValue;
                    current.NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, Math.Pow(2, Math.Min(request.Attempts, 8))) + Random.Shared.NextDouble());
                }
                return true;
            });
            logger.LogWarning("Access request delivery deferred for {RequestId}: {Type}", request.Id, ex.GetType().Name);
        }
        return true;
    }
}
