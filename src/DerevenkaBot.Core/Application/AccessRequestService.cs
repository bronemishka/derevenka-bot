using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Infrastructure;

namespace DerevenkaBot.Core.Application;

public sealed class AccessRequestService(ImageDatabase db, ImageOptions options)
{
    private readonly ImageAccessService access = new(db, options);
    public AccessRequestResult Submit(Actor actor, string displayName, string? username, string key) => db.Run<AccessRequestResult>(s =>
    {
        if (!actor.IsPrivate || actor.UserId <= 0 || actor.ChatId != actor.UserId) return new(null, Texts.RequestInPrivate);
        if (options.OwnerUserId <= 0) return new(null, Texts.RequestsUnavailable);
        if (access.IsAllowed(s, actor)) return new(null, Texts.AlreadyAllowed);
        var repeated = s.AccessRequests.Find(x => x.IdempotencyKey == key);
        if (repeated != null) return new(repeated, repeated.Status == AccessRequestStatus.Pending ? Texts.RequestAlreadySent : Texts.RequestAlreadyReviewed);
        var pending = s.AccessRequests.Find(x => x.Applicant.UserId == actor.UserId && x.Status == AccessRequestStatus.Pending);
        if (pending != null) return new(pending, Texts.RequestAlreadySent);
        var request = new AccessRequest { Applicant = actor, DisplayName = displayName, Username = username, IdempotencyKey = key };
        s.AccessRequests.Add(request);
        return new(request, Texts.RequestSent, true);
    });
    public AccessRequestResult Decide(Actor reviewer, Guid requestId, bool approve) => db.Run<AccessRequestResult>(s =>
    {
        if (!access.IsOwner(reviewer.UserId) || !reviewer.IsPrivate || reviewer.ChatId != options.OwnerUserId)
            return new(null, Texts.OwnerRequired);
        var request = s.AccessRequests.Find(x => x.Id == requestId);
        if (request == null) return new(null, Texts.RequestNotFound);
        if (request.Status != AccessRequestStatus.Pending) return new(request, Texts.RequestAlreadyReviewed);
        request.Status = approve ? AccessRequestStatus.Approved : AccessRequestStatus.Declined;
        request.DecidedAt = DateTimeOffset.UtcNow; request.DecidedBy = reviewer.UserId;
        request.NextAttemptAt = DateTimeOffset.UtcNow; request.Attempts = 0;
        if (approve)
        {
            var grant = new AccessGrant($"user:{request.Applicant.UserId}", "user", request.Applicant.UserId, true,
                reviewer.UserId, DateTimeOffset.UtcNow);
            var index = s.AccessGrants.FindIndex(x => x.Id == grant.Id);
            if (index < 0) s.AccessGrants.Add(grant); else s.AccessGrants[index] = grant;
        }
        // Rejection is a decision about this request, never a revocation of independent permissions.
        return new(request, approve ? Texts.RequestApprovedToast : Texts.RequestDeclinedToast, true);
    });
    public IReadOnlyList<AccessRequest> Pending(Actor reviewer) =>
        access.IsOwner(reviewer.UserId) && reviewer.IsPrivate && reviewer.ChatId == options.OwnerUserId
            ? db.Run(s => s.AccessRequests.Where(x => x.Status == AccessRequestStatus.Pending).OrderBy(x => x.CreatedAt).ToList()) : [];
}
