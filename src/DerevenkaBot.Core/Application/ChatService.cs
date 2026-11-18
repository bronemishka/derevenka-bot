using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Infrastructure;
using Microsoft.Extensions.Logging;

namespace DerevenkaBot.Core.Application;

public sealed class ChatService(ImageDatabase db, ImageOptions images, ChatOptions options,
    IChatProvider provider, ITelegramGateway telegram, ILogger<ChatService> logger)
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> gates = new();
    private readonly ImageAccessService access = new(db, images);
    public bool Enabled => options.Enabled;

    public bool IsReply(Actor actor, int messageId) => db.Run(s => s.Chats
        .Find(x => x.Id == DatabaseState.ActorKey(actor))?.Turns.Any(x => x.MessageIds.Contains(messageId)) == true);

    public async Task EnqueueAsync(Actor actor, int messageId, string prompt, string key, CancellationToken ct)
    {
        if (!await ValidateAsync(actor, messageId, prompt, ct)) return;
        ct.ThrowIfCancellationRequested();
        db.Run(s =>
        {
            if (!access.IsAllowed(s, actor) || s.ChatRequests.Any(x => x.Id == key) || s.Updates.Any(x => x.Id == key)) return false;
            var actorKey = DatabaseState.ActorKey(actor);
            var chat = s.Chats.Find(x => x.Id == actorKey);
            if (chat == null) { chat = new() { Id = actorKey }; s.Chats.Add(chat); }
            s.ChatRequests.Add(new() { Id = key, Actor = actor, MessageId = messageId, Prompt = prompt,
                ConversationVersion = chat.Version, Sequence = s.ChatRequests.Select(x => x.Sequence).DefaultIfEmpty().Max() + 1 });
            return true;
        });
    }

    public void Reset(Actor actor) => db.Run(s =>
    {
        var chat = s.Chats.Find(x => x.Id == DatabaseState.ActorKey(actor));
        if (chat != null) { chat.Version++; chat.Turns.Clear(); }
        foreach (var request in s.ChatRequests.Where(x => x.Actor == actor && x.Status is JobStatus.Queued or JobStatus.Generating))
        { request.Status = JobStatus.Cancelled; request.LeaseOwner = null; }
        return true;
    });

    private async Task<bool> ValidateAsync(Actor actor, int messageId, string prompt, CancellationToken ct)
    {
        if (!Enabled) { await telegram.SendTextAsync(actor.ChatId, messageId, Texts.ChatDisabled, ct); return false; }
        if (prompt.Length > options.MaxInputCharacters)
        {
            await telegram.SendTextAsync(actor.ChatId, messageId,
                $"Сообщение слишком длинное. Сократи его до {options.MaxInputCharacters} символов.", ct);
            return false;
        }
        return true;
    }

    private bool IsCurrent(DatabaseState state, Actor actor, ChatRequest? request) => access.IsAllowed(state, actor) &&
        (request == null || state.ChatRequests.Any(x => x.Id == request.Id && x.LeaseOwner == request.LeaseOwner &&
            x.Status == JobStatus.Generating) && state.Chats.Any(x => x.Id == DatabaseState.ActorKey(actor) && x.Version == request.ConversationVersion));

    public async Task ReplyAsync(Actor actor, int messageId, string prompt, string key, CancellationToken ct, ChatRequest? request = null)
    {
        if (!await ValidateAsync(actor, messageId, prompt, ct)) return;
        var actorKey = DatabaseState.ActorKey(actor);
        var version = request?.ConversationVersion ?? db.Run(s => s.Chats.Find(x => x.Id == actorKey)?.Version ?? 0);
        var gate = gates.GetOrAdd($"{actorKey}:{version}", _ => new(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            if (!access.IsAllowed(actor)) return;
            var state = db.Run(s =>
            {
                if (!IsCurrent(s, actor, request)) return (Chat: (ChatConversation?)null, Duplicate: false);
                var chat = s.Chats.Find(x => x.Id == actorKey);
                if (chat == null) { chat = new() { Id = actorKey }; s.Chats.Add(chat); }
                var duplicate = s.Updates.Any(x => x.Id == key);
                if (!duplicate) s.Updates.Add(new(key));
                return (Chat: (ChatConversation?)chat, Duplicate: duplicate);
            });
            if (state.Chat == null) return;
            var turn = state.Chat.Turns.Find(x => x.Key == key);
            if (state.Duplicate && turn == null) return;
            if (!state.Duplicate)
            {
                var history = state.Chat.Turns.Where(x => !x.Failed).TakeLast(options.HistoryTurns)
                    .SelectMany(x => new[] { new ChatMessage("user", x.Prompt), new ChatMessage("assistant", x.Answer) }).ToList();
                history.Add(new("user", prompt));
                turn = new() { Key = key, Prompt = prompt };
                var snapshot = "";
                using var progress = CancellationTokenSource.CreateLinkedTokenSource(ct);
                var progressTask = ProgressAsync(actor, state.Chat.Version, request, Guid.NewGuid(), () => Volatile.Read(ref snapshot), progress.Token);
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(options.TimeoutSeconds));
                    turn.Answer = await provider.ReplyStreamingAsync(history, text => Volatile.Write(ref snapshot, text), timeout.Token);
                    if (string.IsNullOrWhiteSpace(turn.Answer)) throw new ProviderException("chat_empty_response");
                }
                catch (Exception ex) when (ex is ProviderException or HttpRequestException or JsonException or IOException ||
                    ex is OperationCanceledException && !ct.IsCancellationRequested)
                {
                    logger.LogWarning("Chat provider failed: {Type}", ex.GetType().Name);
                    turn.Answer = Texts.ChatFailure; turn.Failed = true;
                }
                finally
                {
                    await progress.CancelAsync();
                    await progressTask;
                }
                if (!access.IsAllowed(actor)) return;
                if (!db.Run(s =>
                {
                    var chat = s.Chats.Single(x => x.Id == actorKey);
                    if (chat.Version != state.Chat.Version || !IsCurrent(s, actor, request)) return false;
                    chat.Turns.Add(turn);
                    chat.Turns = chat.Turns.TakeLast(options.HistoryTurns).ToList();
                    return true;
                })) return;
            }
            var chunks = Chunks(turn!.Answer).ToArray();
            // A Telegram retry reuses the saved answer, without another paid request.
            for (var i = turn.MessageIds.Count; i < chunks.Length; i++)
            {
                if (!db.Run(s => IsCurrent(s, actor, request) && s.Chats.Single(x => x.Id == actorKey).Version == state.Chat.Version)) return;
                var sent = await telegram.SendChatAnswerAsync(actor, messageId, chunks[i], ct);
                db.Run(s =>
                {
                    if (IsCurrent(s, actor, request)) s.Chats.Single(x => x.Id == actorKey).Turns.Find(x => x.Key == key)?.MessageIds.Add(sent);
                    return true;
                });
            }
        }
        finally { gate.Release(); }
    }

    private async Task ProgressAsync(Actor actor, int version, ChatRequest? request, Guid draftId, Func<string> getText, CancellationToken ct)
    {
        string? previous = null;
        var lastSent = DateTimeOffset.MinValue;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!db.Run(s => IsCurrent(s, actor, request) && s.Chats.Single(x => x.Id == DatabaseState.ActorKey(actor)).Version == version)) return;
                var text = getText();
                try
                {
                    if (text != previous || DateTimeOffset.UtcNow - lastSent >= TimeSpan.FromSeconds(4))
                    {
                        await telegram.ShowChatProgressAsync(actor, draftId, ct, text);
                        previous = text; lastSent = DateTimeOffset.UtcNow;
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                { logger.LogDebug("Chat progress unavailable: {Type}", ex.GetType().Name); }
                await Task.Delay(TimeSpan.FromSeconds(1), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    private static IEnumerable<string> Chunks(string answer)
    {
        var chunk = new StringBuilder();
        foreach (var rune in answer.EnumerateRunes())
        {
            var escaped = Texts.Escape(rune.ToString());
            if (chunk.Length + escaped.Length > 3500) { yield return chunk.ToString(); chunk.Clear(); }
            chunk.Append(escaped);
        }
        if (chunk.Length > 0) yield return chunk.ToString();
    }
}
