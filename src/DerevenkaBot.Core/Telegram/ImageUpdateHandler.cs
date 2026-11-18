using System.Text.Json;
using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Infrastructure;

namespace DerevenkaBot.Core.Telegram;

// Public integration entry point: existing hosts can route their raw Update here without running polling.
public sealed class ImageUpdateHandler(ImageOptions options, TelegramOptions bot, ImageDatabase db,
    ImageJobService jobs, IImageAssetStore assets, ITelegramGateway telegram, ChatService? chats = null)
{
    private readonly ImageAccessService access = new(db, options);
    private readonly ImageConversationService conversation = new(db);
    private readonly AccessRequestService requests = new(db, options);
    public Task<bool> HandleAsync(global::Telegram.Bot.Types.Update update, CancellationToken ct) =>
        HandleAsync(JsonSerializer.SerializeToElement(update, global::Telegram.Bot.JsonBotAPI.Options), ct);
    public async Task<bool> HandleAsync(JsonElement update, CancellationToken ct)
    {
        if (!options.Enabled) return false;
        var key = "update:" + update.GetProperty("update_id").GetInt64();
        if (update.TryGetProperty("callback_query", out var callback)) return await Callback(callback, key, ct);
        if (!update.TryGetProperty("message", out var message) || !message.TryGetProperty("from", out var from) ||
            from.TryGetProperty("is_bot", out var isBot) && isBot.GetBoolean()) return false;
        var actor = GetActor(message, from);
        var messageId = message.GetProperty("message_id").GetInt32();
        var text = message.TryGetProperty("text", out var t) ? t.GetString()! :
            message.TryGetProperty("caption", out t) ? t.GetString()! : "";
        var command = ""; var prompt = text.Trim();
        if (prompt.StartsWith('/') && !message.TryGetProperty("forward_origin", out _))
        {
            var pieces = prompt.Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            var name = pieces[0].Split('@', 2);
            if (name.Length == 2 && !name[1].Equals(bot.Username, StringComparison.OrdinalIgnoreCase)) return false;
            command = name[0].ToLowerInvariant();
            if (command.StartsWith("/ai_")) command = "/" + command[4..];
            if (command == "/start") command = "/help";
            if (command is not ("/chat" or "/imagine" or "/edit" or "/reset" or "/cancel" or "/help" or "/usage" or "/allow" or "/revoke" or "/access" or "/id" or "/request_access" or "/requests")) return false;
            prompt = pieces.Length == 2 ? pieces[1].Trim() : "";
        }
        int? replyId = message.TryGetProperty("reply_to_message", out var reply) ? reply.GetProperty("message_id").GetInt32() : null;
        var file = GetFile(message);
        var replyPrompt = replyId != null ? conversation.FindReply(actor.ChatId, replyId.Value) :
            actor.IsPrivate && command == "" ? conversation.FindActive(actor) : null;
        // A pending /chat invitation also requires an explicit Reply.
        if (replyId == null && replyPrompt?.Action == "chat") replyPrompt = null;
        if (command == "" && replyPrompt != null)
        {
            if (replyPrompt.Owner != actor || replyPrompt.ExpiresAt <= DateTimeOffset.UtcNow)
            {
                await telegram.SendTextAsync(actor.ChatId, messageId, replyPrompt.Owner != actor ? Texts.Denied : Texts.PromptExpired, ct);
                return true;
            }
            command = replyPrompt.Action switch { "chat" => "/chat", "new" => "/imagine", "allow" => "/allow", "revoke" => "/revoke", _ => "/edit" };
            if (command is "/allow" or "/revoke" && message.TryGetProperty("forward_origin", out _))
            {
                var forwardedUser = GetReplyUserId(message);
                if (forwardedUser == null)
                { await telegram.SendTextAsync(actor.ChatId, messageId, Texts.ReplyUserUnavailable, ct); return true; }
                prompt = forwardedUser.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            if (command == "/imagine" && file != null) command = "/edit";
            replyId = replyPrompt.SourceMessageId;
        }
        var knownReply = replyId != null && db.Run(s => s.Messages.Any(x => x.ChatId == actor.ChatId && x.MessageId == replyId));
        var photoReply = replyId != null && GetFile(reply) != null;
        var chatReply = replyId != null && chats?.IsReply(actor, replyId.Value) == true;
        if (command == "" && file == null && !message.TryGetProperty("document", out _) && !knownReply && !photoReply && !chatReply) return false;
        if (command == "/request_access")
        { await RequestAccess(actor, from, messageId, key, ct); return true; }
        if (command == "/requests")
        { await ShowRequests(actor, messageId, ct); return true; }
        if (command == "/id")
        {
            await telegram.SendPanelAsync(actor, messageId, Texts.Identity(actor), BotPanel.Identity, access.IsOwner(actor.UserId), ct); return true;
        }
        if (command is "/allow" or "/revoke" or "/access")
        {
            if (command != "/access" && prompt.Length == 0 && replyId == null && access.IsOwner(actor.UserId))
            {
                if (MarkCallback(key)) await Ask(actor, messageId, command[1..], null, ct);
                return true;
            }
            var targetUser = replyId != null ? GetReplyUserId(reply) : null;
            var response = access.IsOwner(actor.UserId) && command != "/access" && prompt.Length == 0 && replyId != null && targetUser == null
                ? Texts.ReplyUserUnavailable
                : access.Manage(actor, command, prompt, key, targetUser);
            if (command != "/access" && db.Run(s => s.Updates.Any(x => x.Id == key))) conversation.Complete(replyPrompt);
            if (command == "/access" && actor.IsPrivate && access.IsOwner(actor.UserId))
                await telegram.SendPanelAsync(actor, messageId, response, BotPanel.Access, true, ct);
            else await telegram.SendTextAsync(actor.ChatId, messageId, response, ct);
            return true;
        }
        if (!access.IsAllowed(actor))
        { await telegram.SendPanelAsync(actor, messageId, Texts.Denied, BotPanel.Locked, false, ct); return true; }
        if (command == "/chat" || command == "" && chatReply && file == null)
        {
            if (chats?.Enabled != true)
            { await telegram.SendTextAsync(actor.ChatId, messageId, Texts.ChatDisabled, ct); return true; }
            if (file != null || message.TryGetProperty("document", out _))
            { await telegram.SendTextAsync(actor.ChatId, messageId, Texts.ChatTextOnly, ct); return true; }
            if (prompt.Length == 0)
            {
                if (MarkCallback(key)) await Ask(actor, messageId, "chat", null, ct);
                return true;
            }
            conversation.ClearPrompts(actor);
            await chats.EnqueueAsync(actor, messageId, prompt, key, ct);
            return true;
        }
        if (command == "/cancel")
        {
            if (!MarkCallback(key)) return true;
            conversation.Cancel(actor);
            await telegram.SendPanelAsync(actor, messageId, Texts.InputCancelled, BotPanel.Home, access.IsOwner(actor.UserId), ct);
            return true;
        }
        if (command == "/reset")
        {
            if (jobs.Reset(actor, key)) chats?.Reset(actor);
            await telegram.SendPanelAsync(actor, messageId, Texts.Reset, BotPanel.Home, access.IsOwner(actor.UserId), ct); return true;
        }
        if (command is "/help" or "/usage")
        {
            if (command == "/help") conversation.ClearPrompts(actor);
            await telegram.SendPanelAsync(actor, messageId, command == "/help"
                ? Texts.Help + (access.IsOwner(actor.UserId) ? Texts.AdminHelp : "") : jobs.Usage(actor),
                command == "/help" ? BotPanel.Home : BotPanel.Usage, access.IsOwner(actor.UserId), ct); return true;
        }
        if (command is "/imagine" or "/edit" && prompt.Length == 0 && file == null &&
            (replyId == null || knownReply || GetFile(reply) == null))
        {
            if (MarkCallback(key)) await Ask(actor, messageId, command == "/imagine" ? "new" : "edit",
                command == "/edit" && knownReply ? replyId : null, ct);
            return true;
        }
        if (message.TryGetProperty("media_group_id", out _))
        { await telegram.SendTextAsync(actor.ChatId, messageId, Texts.InvalidFile, ct); return true; }
        if (db.Run(s => s.Jobs.Any(x => x.IdempotencyKey == key) || s.Updates.Any(x => x.Id == key))) return true;
        Guid? input = null;
        var sourceFile = file ?? (command is "/edit" or "" && photoReply && !knownReply ? GetFile(reply) : null);
        if (sourceFile != null)
        {
            try
            {
                using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                downloadTimeout.CancelAfter(TimeSpan.FromSeconds(30));
                await using var stream = await telegram.DownloadAsync(sourceFile, downloadTimeout.Token);
                input = (await assets.SaveAsync(stream, sourceFile.MimeType, downloadTimeout.Token)).Id;
                db.Run(s =>
                {
                    var index = s.Assets.FindIndex(x => x.Id == input);
                    s.Assets[index] = s.Assets[index] with { Owner = actor };
                    return true;
                });
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            { await telegram.SendTextAsync(actor.ChatId, messageId, Texts.InvalidFile, ct); return true; }
            if (prompt.Length == 0)
            {
                db.Run(s =>
                {
                    var draft = new Draft(DatabaseState.ActorKey(actor), input.Value, DateTimeOffset.UtcNow.AddMinutes(options.DraftMinutes));
                    var index = s.Drafts.FindIndex(x => x.Id == draft.Id);
                    if (index >= 0) s.Drafts[index] = draft; else s.Drafts.Add(draft);
                    s.Updates.Add(new(key)); return true;
                });
                await Ask(actor, messageId, "edit", null, ct, Texts.DraftSaved(options.DraftMinutes));
                return true;
            }
        }
        if (message.TryGetProperty("document", out _) && file == null)
        { await telegram.SendTextAsync(actor.ChatId, messageId, Texts.InvalidFile, ct); return true; }
        var result = await jobs.EnqueueAsync(new(key, actor, messageId, prompt, command == "/imagine",
            command == "/edit", knownReply && command != "/imagine" ? replyId : null, command == "/imagine" ? null : input), ct);
        if (result.Accepted)
        {
            if (replyPrompt != null) conversation.Complete(replyPrompt); else conversation.ClearPrompts(actor);
        }
        var statusMessageId = await telegram.SendTextAsync(actor.ChatId, messageId, result.Text, ct);
        if (result.Accepted && result.JobId != null)
        {
            await new ImageStatusService(db, telegram).AcknowledgeAsync(result.JobId.Value, statusMessageId, ct);
        }
        return true;
    }
    private async Task<bool> Callback(JsonElement callback, string key, CancellationToken ct)
    {
        var data = callback.TryGetProperty("data", out var d) ? d.GetString() : null;
        if (data == null || !(data.StartsWith("ai:ui:") || data.StartsWith("ai:r:") || data.StartsWith("ai:c:") || data.StartsWith("ai:o:") || data.StartsWith("ai:ar:"))) return false;
        var id = callback.GetProperty("id").GetString()!;
        if (!callback.TryGetProperty("message", out var message))
        { await telegram.AnswerCallbackAsync(id, Texts.Unavailable, ct); return true; }
        var actor = GetActor(message, callback.GetProperty("from"));
        var messageId = message.GetProperty("message_id").GetInt32();
        if (data.StartsWith("ai:ar:"))
        {
            var parts = data.Split(':');
            if (parts.Length != 4 || parts[2] is not ("y" or "n") || !Guid.TryParseExact(parts[3], "N", out var requestId))
            { await telegram.AnswerCallbackAsync(id, Texts.RequestNotFound, ct); return true; }
            var decision = requests.Decide(actor, requestId, parts[2] == "y");
            await telegram.AnswerCallbackAsync(id, decision.Text, ct);
            if (decision.Request != null)
                await telegram.UpdateAccessReviewAsync(actor.ChatId, messageId, decision.Request, ct);
            return true;
        }
        if (data.StartsWith("ai:ui:")) return await Navigate(data[6..], actor, messageId, id, key, callback.GetProperty("from"), ct);
        if (!Guid.TryParseExact(data[5..], "N", out var generation))
        { await telegram.AnswerCallbackAsync(id, Texts.Unavailable, ct); return true; }
        var asset = access.IsAllowed(actor) ? db.Run(s =>
        {
            var link = s.Messages.Find(x => x.ChatId == actor.ChatId && x.MessageId == messageId && x.GenerationId == generation);
            if (link == null || !(link.IsPublic && !actor.IsPrivate || s.Sessions.Any(y => y.Owner == actor &&
                s.Generations.Any(g => g.Id == generation && g.SessionId == y.Id)))) return null;
            return s.Assets.Find(x => x.Id == link.AssetId && x.ExpiresAt > DateTimeOffset.UtcNow);
        }) : null;
        if (asset == null) { await telegram.AnswerCallbackAsync(id, Texts.Unavailable, ct); return true; }
        if (data.StartsWith("ai:r:"))
        {
            jobs.Reset(actor, key);
            await telegram.AnswerCallbackAsync(id, Texts.Reset, ct);
            return true;
        }
        if (!MarkCallback(key)) { await telegram.AnswerCallbackAsync(id, Texts.AlreadyHandled, ct); return true; }
        await telegram.AnswerCallbackAsync(id, data.StartsWith("ai:c:") ? "✏️ Напиши, что изменить" : "📎 Отправляю оригинал…", ct);
        if (data.StartsWith("ai:c:")) await Ask(actor, messageId, "edit", messageId, ct);
        else
        {
            try
            {
                await using var stream = await assets.OpenReadAsync(asset.Id, ct);
                var originalMessageId = await telegram.SendOriginalAsync(actor, messageId, stream, ct);
                db.Run(s =>
                {
                    s.Messages.Add(new($"{actor.ChatId}:{originalMessageId}", actor.ChatId, originalMessageId, generation, asset.Id, !actor.IsPrivate));
                    return true;
                });
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            { await telegram.SendTextAsync(actor.ChatId, messageId, Texts.Unavailable, ct); }
        }
        return true;
    }
    private bool MarkCallback(string key) => db.Run(s =>
    {
        if (s.Updates.Any(x => x.Id == key)) return false;
        s.Updates.Add(new(key)); return true;
    });
    private async Task Ask(Actor actor, int replyMessageId, string action, int? sourceMessageId, CancellationToken ct, string? customText = null)
    {
        if (action == "edit" && customText == null && !HasEditableImage(actor, sourceMessageId)) customText = Texts.UploadFirst;
        var text = customText == null ? Texts.Prompt(action, actor) :
            customText + (actor.IsPrivate ? "" : "\n\n" + Texts.ReplyHint(actor));
        var promptId = await telegram.RequestReplyAsync(actor, replyMessageId, text, ct);
        conversation.Start(new($"{actor.ChatId}:{promptId}", actor, promptId, action, sourceMessageId,
            DateTimeOffset.UtcNow.AddMinutes(options.DraftMinutes)));
    }
    private bool HasEditableImage(Actor actor, int? sourceMessageId) => db.Run(s =>
    {
        var now = DateTimeOffset.UtcNow;
        Guid? image;
        if (sourceMessageId != null)
            image = s.Messages.Find(x => x.ChatId == actor.ChatId && x.MessageId == sourceMessageId)?.AssetId;
        else
        {
            var draft = s.Drafts.Find(x => x.Id == DatabaseState.ActorKey(actor) && x.ExpiresAt > now);
            var active = s.Active.Find(x => x.Id == DatabaseState.ActorKey(actor));
            var head = s.Sessions.Find(x => x.Id == active?.SessionId && !x.Closed)?.Head;
            image = draft?.AssetId ?? s.Generations.Find(x => x.Id == head)?.ResultAssetId;
        }
        return image != null && s.Assets.Any(x => x.Id == image && x.ExpiresAt > now);
    });
    private async Task<bool> Navigate(string action, Actor actor, int messageId, string callbackId, string key, JsonElement requester, CancellationToken ct)
    {
        if (action == "request")
        {
            await telegram.AnswerCallbackAsync(callbackId, actor.IsPrivate ? "📨 Проверяю заявку…" : Texts.RequestInPrivate, ct);
            await RequestAccess(actor, requester, messageId, key, ct); return true;
        }
        if (action == "requests")
        {
            if (!MarkCallback(key)) { await telegram.AnswerCallbackAsync(callbackId, Texts.AlreadyHandled, ct); return true; }
            await telegram.AnswerCallbackAsync(callbackId, "📥", ct); await ShowRequests(actor, messageId, ct); return true;
        }
        if (action != "id" && !access.IsAllowed(actor))
        { await telegram.AnswerCallbackAsync(callbackId, Texts.Denied, ct); return true; }
        if (action is "allow" or "revoke" or "access" && !access.IsOwner(actor.UserId))
        { await telegram.AnswerCallbackAsync(callbackId, Texts.OwnerRequired, ct); return true; }
        if (action is "allow" or "revoke" or "access" && !actor.IsPrivate)
        { await telegram.AnswerCallbackAsync(callbackId, Texts.PrivateAccess, ct); return true; }
        if (action == "chat" && chats?.Enabled != true)
        { await telegram.AnswerCallbackAsync(callbackId, Texts.ChatDisabled, ct); return true; }
        if (action is "chat" or "new" or "edit" or "allow" or "revoke")
        {
            if (!MarkCallback(key)) { await telegram.AnswerCallbackAsync(callbackId, Texts.AlreadyHandled, ct); return true; }
            await telegram.AnswerCallbackAsync(callbackId, "Напиши ответ в сообщении ниже.", ct);
            await Ask(actor, messageId, action, null, ct);
            return true;
        }
        if (action == "reset")
        { if (jobs.Reset(actor, key)) chats?.Reset(actor); await telegram.AnswerCallbackAsync(callbackId, Texts.Reset, ct); return true; }
        if (action == "cancel")
        {
            var pending = conversation.FindReply(actor.ChatId, messageId);
            if (pending == null || pending.Owner != actor || pending.ExpiresAt <= DateTimeOffset.UtcNow)
            { await telegram.AnswerCallbackAsync(callbackId, Texts.PromptExpired, ct); return true; }
            if (!MarkCallback(key)) { await telegram.AnswerCallbackAsync(callbackId, Texts.AlreadyHandled, ct); return true; }
            conversation.Cancel(actor);
            await telegram.AnswerCallbackAsync(callbackId, Texts.InputCancelled, ct);
            // Prompt messages have force_reply=true; update them as plain status rather than changing that field.
            await telegram.EditStatusAsync(actor.ChatId, messageId, Texts.InputCancelled, ct);
            return true;
        }
        var panel = action switch { "images" => BotPanel.Images, "usage" => BotPanel.Usage, "access" => BotPanel.Access, "id" => BotPanel.Identity,
            "privacy" => BotPanel.Privacy, _ => BotPanel.Home };
        var text = panel switch {
            BotPanel.Images => Texts.ImagesHelp, BotPanel.Usage => jobs.Usage(actor), BotPanel.Access => access.Manage(actor, "/access", "", key),
            BotPanel.Identity => Texts.Identity(actor), BotPanel.Privacy => Texts.Privacy(options.RetentionDays),
            _ => Texts.Help + (access.IsOwner(actor.UserId) ? Texts.AdminHelp : "") };
        if (!MarkCallback(key)) { await telegram.AnswerCallbackAsync(callbackId, Texts.AlreadyHandled, ct); return true; }
        if (panel is BotPanel.Home or BotPanel.Images) conversation.ClearPrompts(actor);
        await telegram.AnswerCallbackAsync(callbackId, "👌", ct);
        await telegram.UpdatePanelAsync(actor, messageId, text, panel, access.IsOwner(actor.UserId), ct);
        return true;
    }
    private async Task RequestAccess(Actor actor, JsonElement user, int messageId, string key, CancellationToken ct)
    {
        var name = string.Join(" ", new[] { "first_name", "last_name" }.Select(property =>
            user.TryGetProperty(property, out var value) ? value.GetString() : null).Where(x => !string.IsNullOrWhiteSpace(x)));
        var username = user.TryGetProperty("username", out var nick) ? nick.GetString() : null;
        var result = requests.Submit(actor, name, username, key);
        await telegram.SendPanelAsync(actor, messageId, result.Text, access.IsAllowed(actor) ? BotPanel.Home : BotPanel.Locked,
            access.IsOwner(actor.UserId), ct);
    }
    private async Task ShowRequests(Actor actor, int messageId, CancellationToken ct)
    {
        if (!access.IsOwner(actor.UserId))
        { await telegram.SendTextAsync(actor.ChatId, messageId, Texts.OwnerRequired, ct); return; }
        if (!actor.IsPrivate || actor.ChatId != options.OwnerUserId)
        { await telegram.SendTextAsync(actor.ChatId, messageId, Texts.PrivateAccess, ct); return; }
        await telegram.SendAccessInboxAsync(actor, messageId, requests.Pending(actor), ct);
    }
    private static Actor GetActor(JsonElement message, JsonElement from) => new(
        message.GetProperty("chat").GetProperty("id").GetInt64(), from.GetProperty("id").GetInt64(),
        message.GetProperty("chat").GetProperty("type").GetString() == "private");
    private static long? GetReplyUserId(JsonElement message)
    {
        // For forwarded messages use the visible original author, never the person forwarding it.
        JsonElement user;
        if (message.TryGetProperty("forward_origin", out var origin))
        {
            if (!origin.TryGetProperty("type", out var type) || type.GetString() != "user" ||
                !origin.TryGetProperty("sender_user", out user)) return null;
        }
        else if (message.TryGetProperty("sender_chat", out _) || !message.TryGetProperty("from", out user)) return null;
        if (user.TryGetProperty("is_bot", out var isBot) && isBot.GetBoolean()) return null;
        return user.TryGetProperty("id", out var id) && id.TryGetInt64(out var value) && value > 0 ? value : null;
    }
    private static InputFile? GetFile(JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return null;
        if (message.TryGetProperty("photo", out var photos) && photos.GetArrayLength() > 0)
        {
            var photo = photos[photos.GetArrayLength() - 1];
            return new(photo.GetProperty("file_id").GetString()!, null,
                photo.TryGetProperty("file_size", out var size) ? size.GetInt64() : null);
        }
        if (message.TryGetProperty("document", out var document) && document.TryGetProperty("mime_type", out var mime) &&
            mime.GetString() is "image/png" or "image/jpeg" or "image/webp")
            return new(document.GetProperty("file_id").GetString()!, mime.GetString(),
                document.TryGetProperty("file_size", out var size) ? size.GetInt64() : null);
        return null;
    }
}
