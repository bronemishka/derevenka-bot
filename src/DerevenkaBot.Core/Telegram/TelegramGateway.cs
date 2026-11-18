using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Domain;
using Telegram.Bot;
using Telegram.Bot.Types;
using Telegram.Bot.Types.ReplyMarkups;
using Telegram.Bot.Types.Enums;
using Telegram.Bot.Exceptions;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using TelegramInputFile = Telegram.Bot.Types.InputFile;
using DomainInputFile = DerevenkaBot.Core.Domain.InputFile;

namespace DerevenkaBot.Core.Telegram;

public sealed class TelegramGateway(ITelegramBotClient client, ImageOptions images, TelegramOptions? options = null) : ITelegramGateway
{
    private readonly TelegramOptions settings = options ?? new();
    private readonly ConcurrentDictionary<long, byte> unsupportedDraftChats = new();
    private readonly ConcurrentDictionary<long, DateTimeOffset> progressBackoff = new();
    public async Task<int> SendTextAsync(long chatId, int messageId, string text, CancellationToken ct) =>
        (await client.SendMessage(chatId, text, parseMode: ParseMode.Html, replyParameters: Reply(messageId),
            linkPreviewOptions: new() { IsDisabled = true }, cancellationToken: ct)).Id;

    public async Task<int> SendPanelAsync(Actor actor, int messageId, string text, BotPanel panel, bool isOwner, CancellationToken ct)
    {
        var keyboard = PanelKeyboard(actor, panel, isOwner);
        return text.TrimStart().StartsWith("<h2>", StringComparison.Ordinal)
            ? (await client.SendRichMessage(actor.ChatId, new InputRichMessage { Html = text },
                replyParameters: Reply(messageId), replyMarkup: keyboard, cancellationToken: ct)).Id
            : (await client.SendMessage(actor.ChatId, text, parseMode: ParseMode.Html, replyParameters: Reply(messageId),
                replyMarkup: keyboard, linkPreviewOptions: new() { IsDisabled = true }, cancellationToken: ct)).Id;
    }
    public async Task<int> RequestReplyAsync(Actor actor, int messageId, string text, CancellationToken ct) =>
        (await client.SendMessage(actor.ChatId, text, parseMode: ParseMode.Html, replyParameters: Reply(messageId),
            replyMarkup: actor.IsPrivate
                ? new InlineKeyboardMarkup(Button("↩️ Отмена", "ai:ui:cancel")) { ForceReply = true }
                : new ForceReplyMarkup { Selective = true, InputFieldPlaceholder = "Напиши ответ сюда…" }, cancellationToken: ct)).Id;

    public async Task UpdatePanelAsync(Actor actor, int messageId, string text, BotPanel panel, bool isOwner, CancellationToken ct)
    {
        if (!actor.IsPrivate)
        { await SendPanelAsync(actor, messageId, text, panel, isOwner, ct); return; }
        var rich = text.TrimStart().StartsWith("<h2>", StringComparison.Ordinal);
        try
        {
            // rich_message replaces text in the API; the SDK retains a required text argument for compatibility.
            await client.EditMessageText(actor.ChatId, messageId, rich ? null! : text,
                parseMode: rich ? default : ParseMode.Html, replyMarkup: PanelKeyboard(actor, panel, isOwner),
                richMessage: rich ? new InputRichMessage { Html = text } : null, cancellationToken: ct);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase)) { }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 &&
            (ex.Message.Contains("message to edit not found", StringComparison.OrdinalIgnoreCase) ||
             ex.Message.Contains("message can't be edited", StringComparison.OrdinalIgnoreCase)))
        { await SendPanelAsync(actor, messageId, text, panel, isOwner, ct); }
    }

    public async Task<int> SendOriginalAsync(Actor actor, int messageId, Stream stream, CancellationToken ct) =>
        (await client.SendDocument(actor.ChatId, TelegramInputFile.FromStream(stream, "derevenka-original.png"),
            caption: "📎 <b>Оригинал</b>\nФайл без сжатия Telegram.", parseMode: ParseMode.Html,
            replyParameters: Reply(messageId), cancellationToken: ct)).Id;

    public async Task EditStatusAsync(long chatId, int messageId, string text, CancellationToken ct) =>
        _ = await client.EditMessageText(chatId, messageId, text, parseMode: ParseMode.Html,
            linkPreviewOptions: new() { IsDisabled = true }, cancellationToken: ct);
    public Task ShowGeneratingAsync(long chatId, CancellationToken ct) =>
        client.SendChatAction(chatId, ChatAction.UploadPhoto, cancellationToken: ct);
    public Task ShowTypingAsync(long chatId, CancellationToken ct) =>
        client.SendChatAction(chatId, ChatAction.Typing, cancellationToken: ct);

    public async Task ShowChatProgressAsync(Actor actor, Guid draft, CancellationToken ct, string? text = null)
    {
        if (progressBackoff.TryGetValue(actor.ChatId, out var until) && until > DateTimeOffset.UtcNow) return;
        try
        {
            if (settings.UseAiDrafts && actor.IsPrivate && !unsupportedDraftChats.ContainsKey(actor.ChatId))
            {
                var draftId = BinaryPrimitives.ReadInt64LittleEndian(draft.ToByteArray()) & long.MaxValue;
                try
                {
                    await client.SendRichMessageDraft(actor.ChatId, draftId == 0 ? 1 : draftId,
                        new InputRichMessage { Html = string.IsNullOrEmpty(text)
                            ? "<tg-thinking>💬 Думаю над ответом…</tg-thinking>" : ChatHtml(Preview(text)) },
                        canStop: false, keepOnStop: false, cancellationToken: ct);
                    return;
                }
                catch (ApiRequestException ex) when (ex.ErrorCode is 400 or 404)
                { unsupportedDraftChats.TryAdd(actor.ChatId, 0); }
            }
            await ShowTypingAsync(actor.ChatId, ct);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 429)
        { progressBackoff[actor.ChatId] = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, ex.Parameters?.RetryAfter ?? 30)); }
    }

    public async Task<int> SendChatAnswerAsync(Actor actor, int messageId, string escapedText, CancellationToken ct) =>
        settings.UseAiDrafts && actor.IsPrivate && !unsupportedDraftChats.ContainsKey(actor.ChatId)
            ? (await client.SendRichMessage(actor.ChatId,
                new InputRichMessage { Html = "<p>" + escapedText.Replace("\n", "<br>") + "</p>" },
                replyParameters: Reply(messageId), cancellationToken: ct)).Id
            : await SendTextAsync(actor.ChatId, messageId, escapedText, ct);

    private static string ChatHtml(string text) => "<p>" + Texts.Escape(text).Replace("\n", "<br>") + "</p>";
    private static string Preview(string text)
    {
        if (text.Length <= 3500) return text;
        var start = text.Length - 3500;
        if (char.IsLowSurrogate(text[start])) start++;
        return "…" + text[start..];
    }

    public async Task<GenerationProgressMode> ShowGenerationProgressAsync(ImageJob job, CancellationToken ct)
    {
        var chatId = job.Actor.ChatId;
        if (progressBackoff.TryGetValue(chatId, out var until) && until > DateTimeOffset.UtcNow)
            return job.NativeProgress ? GenerationProgressMode.NativeDraft : GenerationProgressMode.StatusMessage;
        try
        {
            if (settings.UseAiDrafts && job.Actor.IsPrivate && !unsupportedDraftChats.ContainsKey(chatId))
            {
                var draftId = BinaryPrimitives.ReadInt64LittleEndian(job.Id.ToByteArray()) & long.MaxValue;
                try
                {
                    await client.SendRichMessageDraft(chatId, draftId == 0 ? 1 : draftId,
                        new InputRichMessage { Html = "<tg-thinking>🎨 Создаю изображение…</tg-thinking>" },
                        canStop: false, keepOnStop: false, cancellationToken: ct);
                    return GenerationProgressMode.NativeDraft;
                }
                catch (ApiRequestException ex) when (ex.ErrorCode is 400 or 404)
                {
                    // Some local Bot API servers do not support live drafts yet. Keep the normal status working.
                    unsupportedDraftChats.TryAdd(chatId, 0);
                }
            }
            await ShowGeneratingAsync(chatId, ct);
            return GenerationProgressMode.StatusMessage;
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 429)
        {
            progressBackoff[chatId] = DateTimeOffset.UtcNow.AddSeconds(Math.Max(1, ex.Parameters?.RetryAfter ?? 30));
            return job.NativeProgress ? GenerationProgressMode.NativeDraft : GenerationProgressMode.StatusMessage;
        }
    }
    public async Task DeleteStatusAsync(long chatId, int messageId, CancellationToken ct)
    {
        try { await client.DeleteMessage(chatId, messageId, ct); }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("message to delete not found", StringComparison.OrdinalIgnoreCase)) { }
    }

    private static InlineKeyboardButton Button(string label, string data, KeyboardButtonStyle? style = null) =>
        new() { Text = label, CallbackData = data, Style = style };
    private InlineKeyboardMarkup PanelKeyboard(Actor actor, BotPanel panel, bool isOwner)
    {
        var rows = new List<InlineKeyboardButton[]>();
        if (panel == BotPanel.Locked)
        {
            rows.Add(actor.IsPrivate || string.IsNullOrWhiteSpace(settings.Username)
                ? [Button("📨 Запросить доступ", "ai:ui:request", KeyboardButtonStyle.Primary)]
                : [InlineKeyboardButton.WithUrl("📨 Запросить доступ в личке", $"https://t.me/{Uri.EscapeDataString(settings.Username)}?start=access")]);
            rows.Add([new() { Text = "📋 Скопировать мой ID", CopyText = new CopyTextButton { Text = actor.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture) } }]);
            return new(rows);
        }
        if (panel == BotPanel.Home)
        {
            rows.Add([Button("💬 Чат", "ai:ui:chat", KeyboardButtonStyle.Primary), Button("🎨 Картинки", "ai:ui:images", KeyboardButtonStyle.Primary)]);
            rows.Add([Button("📊 Статистика", "ai:ui:usage"), Button("🪪 Мой ID", "ai:ui:id")]);
            rows.Add([Button("🗂 История и данные", "ai:ui:privacy"), Button("🆕 Начать заново", "ai:ui:reset")]);
            if (isOwner && actor.IsPrivate) rows.Add([Button("🔑 Управление доступом", "ai:ui:access")]);
        }
        else
        {
            if (panel == BotPanel.Images)
                rows.Add([Button("✨ Создать картинку", "ai:ui:new", KeyboardButtonStyle.Primary), Button("✏️ Изменить фото", "ai:ui:edit")]);
            if (panel == BotPanel.Identity)
                rows.Add([new() { Text = "📋 User ID", CopyText = new CopyTextButton { Text = actor.UserId.ToString(System.Globalization.CultureInfo.InvariantCulture) } },
                    new() { Text = "📋 Chat ID", CopyText = new CopyTextButton { Text = actor.ChatId.ToString(System.Globalization.CultureInfo.InvariantCulture) } }]);
            if (panel == BotPanel.Access && isOwner && actor.IsPrivate)
            {
                rows.Add([Button("🤝 Добавить друга", "ai:ui:allow", KeyboardButtonStyle.Success), Button("🔐 Отозвать", "ai:ui:revoke", KeyboardButtonStyle.Danger)]);
                rows.Add([Button("📥 Заявки на доступ", "ai:ui:requests")]);
            }
            if (panel is BotPanel.Usage or BotPanel.Access)
                rows.Add([Button("🔄 Обновить", panel == BotPanel.Usage ? "ai:ui:usage" : "ai:ui:access")]);
            rows.Add([Button("🏠 Главное меню", "ai:ui:help")]);
        }
        return new(rows);
    }

    private static InlineKeyboardButton[] ReviewButtons(AccessRequest request, string? label = null) =>
    [Button("✅ " + (label ?? "Разрешить"), "ai:ar:y:" + request.Id.ToString("N"), KeyboardButtonStyle.Success),
        Button("❌ " + (label ?? "Отклонить"), "ai:ar:n:" + request.Id.ToString("N"), KeyboardButtonStyle.Danger)];
    public async Task<int> SendAccessReviewAsync(long ownerId, AccessRequest request, CancellationToken ct) =>
        (await client.SendMessage(ownerId, Texts.AccessRequestCard(request), parseMode: ParseMode.Html,
            replyMarkup: new InlineKeyboardMarkup(ReviewButtons(request)), linkPreviewOptions: new() { IsDisabled = true }, cancellationToken: ct)).Id;
    public async Task SendAccessDecisionAsync(AccessRequest request, CancellationToken ct) =>
        _ = await client.SendMessage(request.Applicant.ChatId, Texts.AccessDecision(request), parseMode: ParseMode.Html,
            replyMarkup: PanelKeyboard(request.Applicant, request.Status == AccessRequestStatus.Approved ? BotPanel.Home : BotPanel.Locked, false),
            cancellationToken: ct);
    public async Task UpdateAccessReviewAsync(long chatId, int messageId, AccessRequest request, CancellationToken ct)
    {
        try
        {
            await client.EditMessageText(chatId, messageId, Texts.AccessRequestCard(request), parseMode: ParseMode.Html,
                replyMarkup: new InlineKeyboardMarkup(Button("📥 Другие заявки", "ai:ui:requests")), cancellationToken: ct);
        }
        catch (ApiRequestException ex) when (ex.ErrorCode == 400 && ex.Message.Contains("message is not modified", StringComparison.OrdinalIgnoreCase)) { }
    }
    public async Task SendAccessInboxAsync(Actor reviewer, int messageId, IReadOnlyList<AccessRequest> requests, CancellationToken ct)
    {
        var rows = requests.Take(10).Select((request, i) => ReviewButtons(request, $"№ {i + 1}")).ToList();
        rows.Add([Button("🔄 Обновить заявки", "ai:ui:requests"), Button("🔑 Все разрешения", "ai:ui:access")]);
        await client.SendMessage(reviewer.ChatId, Texts.AccessInbox(requests), parseMode: ParseMode.Html,
            replyParameters: messageId > 0 ? Reply(messageId) : null, replyMarkup: new InlineKeyboardMarkup(rows), cancellationToken: ct);
    }

    public async Task<int> PublishAsync(ImageJob job, StoredAsset asset, Stream stream, CancellationToken ct)
    {
        var document = asset.SizeBytes > 10 * 1024 * 1024 || asset.Width + asset.Height > 10000 ||
            Math.Max(asset.Width, asset.Height) > 20 * Math.Min(asset.Width, asset.Height);
        var generationId = job.GenerationId.ToString("N");
        var markup = new InlineKeyboardMarkup(new[] {
            new[] { Button("✏️ Продолжить", "ai:c:" + generationId, KeyboardButtonStyle.Primary), Button("📎 Оригинал", "ai:o:" + generationId) },
            new[] { Button("🆕 Новая сессия", "ai:r:" + generationId) } });
        var file = TelegramInputFile.FromStream(stream, "image.png");
        var message = document
            ? await client.SendDocument(job.Actor.ChatId, file, caption: Texts.Ready, parseMode: ParseMode.Html, replyParameters: Reply(job.MessageId), replyMarkup: markup, cancellationToken: ct)
            : await client.SendPhoto(job.Actor.ChatId, file, caption: Texts.Ready, parseMode: ParseMode.Html, replyParameters: Reply(job.MessageId), replyMarkup: markup, cancellationToken: ct);
        return message.Id;
    }
    public async Task<Stream> DownloadAsync(DomainInputFile file, CancellationToken ct)
    {
        var max = images.MaxInputFileMb * 1024L * 1024;
        if (file.Size > max) throw new InvalidDataException("file_size");
        var info = await client.GetFile(file.FileId, ct);
        if (info.FileSize > max || string.IsNullOrEmpty(info.FilePath)) throw new InvalidDataException("file_size_or_path");
        if (info.FilePath.StartsWith('/') || info.FilePath.Split('/').Any(x => x is ".." or "." || x.Contains('\\') || x.Contains(':')))
            throw new InvalidDataException("file_path");
        var result = new BoundedDownloadStream(max);
        try
        {
            await client.DownloadFile(info, result, ct);
            result.Position = 0;
            return result;
        }
        catch { result.Dispose(); throw; }
    }
    public Task AnswerCallbackAsync(string id, string text, CancellationToken ct) =>
        client.AnswerCallbackQuery(id, Texts.Toast(text), cancellationToken: ct);
    private static ReplyParameters Reply(int id) => new() { MessageId = id, AllowSendingWithoutReply = true };
    private sealed class BoundedDownloadStream(long max) : MemoryStream
    {
        private void Check(int count) { if (Position + count > max) throw new InvalidDataException("file_size"); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); base.Write(buffer, offset, count); }
        public override void Write(ReadOnlySpan<byte> buffer) { Check(buffer.Length); base.Write(buffer); }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        { Check(count); return base.WriteAsync(buffer, offset, count, ct); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        { Check(buffer.Length); return base.WriteAsync(buffer, ct); }
    }
}
