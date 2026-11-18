using System.Globalization;
using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Infrastructure;

namespace DerevenkaBot.Core.Application;

public sealed class ImageAccessService(ImageDatabase db, ImageOptions options)
{
    public bool IsOwner(long userId) => options.OwnerUserId > 0 && userId == options.OwnerUserId;
    public bool IsAllowed(Actor actor) => db.Run(s => IsAllowed(s, actor));
    public bool IsAllowed(DatabaseState state, Actor actor)
    {
        if (IsOwner(actor.UserId)) return true;
        var user = state.AccessGrants.Find(x => x.Kind == "user" && x.TargetId == actor.UserId);
        var chat = state.AccessGrants.Find(x => x.Kind == "chat" && x.TargetId == actor.ChatId);
        return (user?.Allowed ?? options.AllowedUsers.Contains(actor.UserId)) ||
            (!actor.IsPrivate && (chat?.Allowed ?? options.AllowedChats.Contains(actor.ChatId)));
    }
    public string Manage(Actor actor, string command, string arguments, string updateKey, long? replyUserId = null)
    {
        if (!IsOwner(actor.UserId)) return Texts.OwnerRequired;
        if (command is not ("/allow" or "/revoke" or "/access")) return Texts.AccessSyntax;
        if (command == "/access")
        {
            if (!actor.IsPrivate) return Texts.PrivateAccess;
            return db.Run(s =>
            {
                var grants = options.AllowedUsers.Select(x => new AccessGrant($"user:{x}", "user", x, true, 0, DateTimeOffset.MinValue))
                    .Concat(options.AllowedChats.Select(x => new AccessGrant($"chat:{x}", "chat", x, true, 0, DateTimeOffset.MinValue)))
                    .Concat(s.AccessGrants).GroupBy(x => x.Id).Select(x => x.Last()).OrderByDescending(x => x.Allowed).ToList();
                var rows = grants.Take(40).Select(x => $"<tr><td>{(x.Kind == "user" ? "👤 Друг" : "💬 Группа")}</td><td><code>{x.TargetId}</code></td><td>{(x.Allowed ? "✅ Есть доступ" : "🔐 Отозвано")}</td></tr>");
                var list = grants.Count == 0 ? "<p>Пока доступ есть только у тебя. Чтобы дать доступ другому человеку, нажми «Добавить друга».</p>" :
                    "<table><tr><th>Кому</th><th>ID</th><th>Статус</th></tr>" + string.Join("", rows) + "</table>";
                var more = grants.Count > 40 ? $"<p>Показаны первые 40 из {grants.Count} записей.</p>" : "";
                return $"<h2>🔑 Управление доступом</h2><p>Открытых разрешений: <b>{grants.Count(x => x.Allowed)}</b></p>{list}{more}<footer>Доступ человека и группы настраивается отдельно. Менять доступ можешь только ты.</footer>";
            });
        }
        var parts = arguments.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        string kind; long target;
        if (parts is ["here"] && !actor.IsPrivate) { kind = "chat"; target = actor.ChatId; }
        else if (parts.Length == 0 && replyUserId is > 0) { kind = "user"; target = replyUserId.Value; }
        else if (parts.Length == 1 && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out target) && target > 0)
            kind = "user";
        else if (parts.Length == 2 && parts[0] == "user" &&
                 long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out target) && target > 0)
            kind = "user";
        else if (actor.IsPrivate && parts.Length == 2 && parts[0] == "chat" &&
                 long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out target) &&
                 target < 0) kind = "chat";
        else return Texts.AccessSyntax;
        return db.Run(s =>
        {
            if (s.Updates.Any(x => x.Id == updateKey)) return Texts.AlreadyHandled;
            var grant = new AccessGrant($"{kind}:{target}", kind, target, command == "/allow", actor.UserId, DateTimeOffset.UtcNow);
            var index = s.AccessGrants.FindIndex(x => x.Id == grant.Id);
            if (index < 0) s.AccessGrants.Add(grant); else s.AccessGrants[index] = grant;
            if (grant.Allowed && kind == "user")
            {
                foreach (var request in s.AccessRequests.Where(x => x.Applicant.UserId == target && x.Status == AccessRequestStatus.Pending))
                {
                    request.Status = AccessRequestStatus.Approved; request.DecidedAt = DateTimeOffset.UtcNow;
                    request.DecidedBy = actor.UserId; request.NextAttemptAt = DateTimeOffset.UtcNow; request.Attempts = 0;
                }
            }
            s.Updates.Add(new(updateKey));
            if (!grant.Allowed)
            {
                foreach (var job in s.Jobs.Where(x => x.Status == JobStatus.Queued && !IsAllowed(s, x.Actor)))
                { job.Status = JobStatus.Cancelled; job.ErrorCode = "access_revoked"; s.Usage.Single(x => x.Id == job.Id).Released = true; }
            }
            return $"{(grant.Allowed ? "✅ <b>Доступ выдан</b>" : "🔐 <b>Разрешение отозвано</b>")}\n\n{(kind == "user" ? "👤 Друг" : "💬 Группа")}: <code>{target}</code>\n" +
                (grant.Allowed ? "Можно общаться с ботом и создавать картинки." : "Остальные разрешения пользователя и группы сохраняются.");
        });
    }
}
