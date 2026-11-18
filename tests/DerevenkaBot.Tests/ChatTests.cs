using System.Collections.Concurrent;
using System.Net;
using System.Text.Json;
using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Infrastructure;
using DerevenkaBot.Core.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;

namespace DerevenkaBot.Tests;

public sealed class ChatTests : IDisposable
{
    private readonly ImageOptions images = new() { Enabled = true, AllowedUsers = [1, 2],
        StoragePath = Path.Combine(Path.GetTempPath(), "derevenka-tests", Guid.NewGuid().ToString("N")) };
    private readonly ChatOptions options = new();
    private readonly ImageDatabase db;
    private readonly Provider provider = new();
    private readonly Gateway telegram = new();
    private readonly ChatService chats;
    private readonly ImageUpdateHandler handler;
    private static readonly Actor User = new(1, 1, true);

    public ChatTests()
    {
        db = new(images); db.Migrate();
        chats = Service(db);
        handler = Handler(db, chats);
    }
    private ChatService Service(ImageDatabase database) => new(database, images, options, provider, telegram, NullLogger<ChatService>.Instance);
    private ImageUpdateHandler Handler(ImageDatabase database, ChatService service) => new(images, new() { Username = "TestBot" },
        database, new(database, images), new LocalAssetStore(images, database), telegram, service);
    private ChatJobWorker Worker(ImageDatabase? database = null, ChatService? service = null) =>
        new(database ?? db, images, options, service ?? chats, NullLogger<ChatJobWorker>.Instance);
    private async Task<bool> DispatchAsync(JsonElement update, CancellationToken ct)
    {
        var accepted = await handler.HandleAsync(update, ct);
        var worker = Worker();
        while (await worker.ProcessOneAsync(ct)) { }
        return accepted;
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChatMenuButtonInvitesReplyWithoutCallingModelAndDuplicateClickIsIgnored(bool isPrivate)
    {
        var actor = new Actor(isPrivate ? 1 : -100, 1, isPrivate);
        var click = Callback("chat", actor, 1);
        await handler.HandleAsync(click, default);
        var prompt = Assert.Single(db.Run(s => s.Prompts));
        Assert.Equal("chat", prompt.Action);
        Assert.Empty(provider.Calls); Assert.Empty(db.Run(s => s.ChatRequests));
        await handler.HandleAsync(click, default);
        Assert.Single(telegram.Sent);
        Assert.False(await handler.HandleAsync(Update("обычное сообщение", 2, actor), default));
        await DispatchAsync(Update("Почему идёт дождь?", 3, actor, prompt.MessageId), default);
        Assert.Single(provider.Calls);
        Assert.Empty(db.Run(s => s.Jobs));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ImagesMenuCancelsPendingChatInvitationAndOpensImageCreation(bool isPrivate)
    {
        var actor = new Actor(isPrivate ? 1 : -100, 1, isPrivate);
        await handler.HandleAsync(Update("/chat", 1, actor), default);
        await handler.HandleAsync(Callback("images", actor, 2), default);
        Assert.Equal(Texts.ImagesHelp, telegram.Sent.Last().Text);
        Assert.Null(new ImageConversationService(db).FindActive(actor));
        Assert.Empty(provider.Calls); Assert.Empty(db.Run(s => s.ChatRequests));
        await handler.HandleAsync(Callback("new", actor, 3), default);
        var prompt = new ImageConversationService(db).FindActive(actor)!;
        Assert.Equal("new", prompt.Action);
        await handler.HandleAsync(Update("дом у озера", 4, actor, prompt.MessageId), default);
        Assert.Single(db.Run(s => s.Jobs));
        Assert.Empty(provider.Calls);
        await handler.HandleAsync(Callback("help", actor, 5), default);
        Assert.Equal(Texts.Help, telegram.Sent.Last().Text);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ChatMenuRespectsDisabledChatAndAccessPermissions(bool disabled)
    {
        if (disabled) options.Enabled = false; else images.AllowedUsers = [];
        await handler.HandleAsync(Callback("chat", User, 1), default);
        Assert.Equal(disabled ? Texts.ChatDisabled : Texts.Denied, telegram.CallbackTexts.Single());
        Assert.Empty(db.Run(s => s.Prompts));
        Assert.Empty(db.Run(s => s.ChatRequests));
        Assert.Empty(provider.Calls);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StartsWithCommandAndContinuesOnlyByReply(bool isPrivate)
    {
        var actor = new Actor(isPrivate ? 1 : -100, 1, isPrivate);
        Assert.False(await DispatchAsync(Update("привет", 1, actor), default));
        Assert.True(await DispatchAsync(Update("/chat Привет!", 2, actor), default));
        var answerId = telegram.Sent.Last().Id;
        Assert.False(await DispatchAsync(Update("как дела?", 3, actor), default));
        Assert.True(await DispatchAsync(Update("как дела?", 4, actor, answerId), default));
        Assert.Equal(2, provider.Calls.Count);
        Assert.Equal(new[] { "user", "assistant", "user" }, provider.Calls.Last().Select(x => x.Role));
        Assert.Equal("Привет!", provider.Calls.Last()[0].Content);
        Assert.Empty(db.Run(s => s.Jobs));
        Assert.Equal(2, telegram.Progress.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task EmptyChatCommandInvitesReplyAndIgnoresPlainMessages(bool isPrivate)
    {
        var actor = new Actor(isPrivate ? 1 : -100, 1, isPrivate);
        await DispatchAsync(Update("/chat", 1, actor), default);
        var invitation = telegram.Sent.Single().Id;
        Assert.Empty(provider.Calls);
        Assert.False(await DispatchAsync(Update("вопрос без Reply", 2, actor), default));
        Assert.True(await DispatchAsync(Update("Что такое радуга?", 3, actor, invitation), default));
        Assert.Equal("Что такое радуга?", Assert.Single(Assert.Single(provider.Calls)).Content);
        Assert.Empty(db.Run(s => s.Jobs));
    }

    [Fact]
    public async Task RestartPreservesHistoryAndReplyRouting()
    {
        await DispatchAsync(Update("/chat Меня зовут Маша", 1), default);
        var reply = telegram.Sent.Single().Id;
        var restored = new ImageDatabase(images); restored.Migrate();
        var service = Service(restored);
        await Handler(restored, service).HandleAsync(Update("Как меня зовут?", 2, replyId: reply), default);
        Assert.True(await Worker(restored, service).ProcessOneAsync(default));
        Assert.Equal("Меня зовут Маша", provider.Calls.Last()[0].Content);
        Assert.Equal(3, provider.Calls.Last().Count);
    }

    [Fact]
    public async Task UserAndChatHistoriesAreIsolatedAndOtherUsersCannotReplyToPrivateHistory()
    {
        await DispatchAsync(Update("/chat личный секрет", 1), default);
        var reply = telegram.Sent.Single().Id;
        Assert.False(await DispatchAsync(Update("Расскажи секрет", 2, new(1, 2, true), reply), default));
        await DispatchAsync(Update("/chat другой человек", 3, new(2, 2, true)), default);
        await DispatchAsync(Update("/chat группа", 4, new(-100, 1, false)), default);
        Assert.All(provider.Calls, call => Assert.Single(call));
        Assert.Equal(3, db.Run(s => s.Chats.Count));
    }

    [Fact]
    public async Task ResetClearsConversationAndDuplicateResetDoesNotClearNewMessages()
    {
        await DispatchAsync(Update("/chat старый вопрос", 1), default);
        var oldReply = telegram.Sent.Single().Id;
        var reset = Update("/reset", 2);
        await DispatchAsync(reset, default);
        Assert.False(await DispatchAsync(Update("продолжение", 3, replyId: oldReply), default));
        await DispatchAsync(Update("/chat новый вопрос", 4), default);
        await DispatchAsync(reset, default);
        var current = Assert.Single(db.Run(s => s.Chats.Single().Turns));
        Assert.Equal("новый вопрос", current.Prompt);
        Assert.Single(provider.Calls.Last());
    }

    [Fact]
    public async Task DuplicateUpdateDoesNotRepeatProviderOrDelivery()
    {
        var update = Update("/chat вопрос", 1);
        await Task.WhenAll(DispatchAsync(update, default), DispatchAsync(update, default));
        Assert.Single(provider.Calls); Assert.Single(telegram.Sent);
    }

    [Fact]
    public async Task FailedTelegramDeliveryRetriesSavedAnswerWithoutPaidRequest()
    {
        telegram.FailSend = true;
        var update = Update("/chat вопрос", 1);
        await DispatchAsync(update, default);
        Assert.Empty(telegram.Sent);
        Assert.Equal(JobStatus.Queued, db.Run(s => s.ChatRequests.Single().Status));
        telegram.FailSend = false;
        db.Run(s => { s.ChatRequests.Single().NextAttemptAt = DateTimeOffset.MinValue; return true; });
        await DispatchAsync(update, default);
        Assert.Single(provider.Calls); Assert.Single(telegram.Sent);
    }

    [Fact]
    public async Task FailedProviderDoesNotRetryAutomaticallyOrPolluteHistory()
    {
        provider.Fail = true;
        var update = Update("/chat вопрос", 1);
        await DispatchAsync(update, default);
        await DispatchAsync(update, default);
        Assert.Single(provider.Calls);
        Assert.Equal(Texts.ChatFailure, telegram.Sent.Single().Text);
        provider.Fail = false;
        await DispatchAsync(Update("/chat другой вопрос", 2), default);
        Assert.Single(provider.Calls.Last());
    }

    [Fact]
    public async Task LongAnswerIsEscapedAndSplitWithoutBreakingEmoji()
    {
        provider.Answer = "<b>&" + string.Concat(Enumerable.Repeat("🌲", 4000));
        await DispatchAsync(Update("/chat длинный ответ", 1), default);
        Assert.True(telegram.Sent.Count > 1);
        Assert.All(telegram.Sent, x => Assert.InRange(x.Text.Length, 1, 3500));
        Assert.Equal(provider.Answer, string.Concat(telegram.Sent.Select(x => Texts.Plain(x.Text))));
        Assert.StartsWith("&lt;b&gt;&amp;", telegram.Sent[0].Text);
        Assert.All(telegram.Sent, x => Assert.True(chats.IsReply(User, x.Id)));
    }

    [Fact]
    public async Task DisabledOrUnauthorizedChatDoesNotCallProvider()
    {
        options.Enabled = false;
        await DispatchAsync(Update("/chat привет", 1), default);
        Assert.Equal(Texts.ChatDisabled, telegram.Sent.Single().Text);
        options.Enabled = true; images.AllowedUsers = [];
        await DispatchAsync(Update("/chat привет", 2), default);
        Assert.Equal(Texts.Denied, telegram.Sent.Last().Text);
        Assert.Empty(provider.Calls);
    }

    [Fact]
    public async Task HistoryIsBoundedAndLongInputDoesNotCallProvider()
    {
        options.HistoryTurns = 2; options.MaxInputCharacters = 12;
        await DispatchAsync(Update("/chat " + new string('я', 13), 1), default);
        Assert.Empty(provider.Calls);
        for (var i = 2; i < 7; i++) await DispatchAsync(Update("/chat вопрос " + i, i), default);
        Assert.Equal(2, db.Run(s => s.Chats.Single().Turns.Count));
        Assert.Equal(5, provider.Calls.Last().Count);
    }

    [Fact]
    public async Task GeneratedTextAppearsInDraftBeforeProviderFinishes()
    {
        provider.Partial = "Ответ уже генерируется";
        provider.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = DispatchAsync(Update("/chat вопрос", 1), default);
        var partial = await telegram.TextDraft.Task.WaitAsync(TimeSpan.FromSeconds(8));
        Assert.False(task.IsCompleted);
        Assert.Empty(telegram.Sent);
        Assert.Equal(provider.Partial, partial);
        Assert.Single(telegram.Progress.Distinct());
        provider.Block.SetResult("Ответ уже генерируется и теперь готов.");
        await task;
        Assert.Equal("Ответ уже генерируется и теперь готов.", telegram.Sent.Single().Text);
    }

    [Fact]
    public async Task ProgressFailureDoesNotPreventAnswer()
    {
        telegram.FailProgress = true;
        await DispatchAsync(Update("/chat привет", 1), default);
        Assert.Single(provider.Calls); Assert.Single(telegram.Sent);
    }

    [Fact]
    public async Task ResetDuringRequestDiscardsStaleAnswer()
    {
        provider.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = DispatchAsync(Update("/chat вопрос", 1), default);
        await provider.Started.Task;
        await DispatchAsync(Update("/reset", 2), default);
        provider.Block.SetResult("старый ответ");
        await task;
        Assert.DoesNotContain(telegram.Sent, x => x.Text == "старый ответ");
        Assert.Empty(db.Run(s => s.Chats.Single().Turns));
    }

    [Fact]
    public async Task RevocationDuringRequestPreventsAnswerDelivery()
    {
        provider.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = DispatchAsync(Update("/chat вопрос", 1), default);
        await provider.Started.Task;
        images.AllowedUsers = [];
        provider.Block.SetResult("секрет");
        await task;
        Assert.Empty(telegram.Sent);
    }

    [Theory]
    [InlineData("/chat@TestBot привет", true)]
    [InlineData("/ai_chat привет", true)]
    [InlineData("/chat@OtherBot привет", false)]
    public async Task ChatSupportsAddressedCommandsAndAliases(string text, bool accepted)
    {
        Assert.Equal(accepted, await DispatchAsync(Update(text, 1), default));
        Assert.Equal(accepted ? 1 : 0, provider.Calls.Count);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task SlowRequestDoesNotBlockOtherUsersOrOtherChats(bool sameUser)
    {
        provider.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondActor = sameUser ? new Actor(-100, 1, false) : new Actor(2, 2, true);
        await handler.HandleAsync(Update("/chat первый", 1), default);
        await handler.HandleAsync(Update("/chat второй", 2, secondActor), default);
        Assert.Empty(provider.Calls);
        var first = Worker().ProcessOneAsync(default);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var second = Worker().ProcessOneAsync(default);
        try
        {
            await provider.StartedTwice.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(first.IsCompleted); Assert.False(second.IsCompleted);
            Assert.Equal(2, db.Run(s => s.ChatRequests.Count(x => x.Status == JobStatus.Generating)));
            await handler.HandleAsync(Update("/usage", 3), default);
            Assert.Single(telegram.Sent);
        }
        finally { provider.Block.SetResult("готово"); await Task.WhenAll(first, second); }
        Assert.All(provider.Calls, x => Assert.Single(x));
        Assert.All(db.Run(s => s.ChatRequests), x => Assert.Equal(JobStatus.Completed, x.Status));
    }

    [Fact]
    public async Task QueuedSameConversationKeepsOrderWithoutBlockingAnotherConversation()
    {
        provider.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await handler.HandleAsync(Update("/chat первый вопрос", 1), default);
        await handler.HandleAsync(Update("/chat второй вопрос", 2), default);
        await handler.HandleAsync(Update("/chat другой человек", 3, new(2, 2, true)), default);
        var first = Worker().ProcessOneAsync(default);
        var other = Worker().ProcessOneAsync(default);
        try
        {
            await provider.StartedTwice.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("другой человек", provider.Calls.Last().Last().Content);
            Assert.False(await Worker().ProcessOneAsync(default));
            Assert.Equal(JobStatus.Queued, db.Run(s => s.ChatRequests.Single(x => x.Id == "update:2").Status));
        }
        finally { provider.Block.SetResult("первый ответ"); await Task.WhenAll(first, other); }
        Assert.True(await Worker().ProcessOneAsync(default));
        Assert.Equal(new[] { "первый вопрос", "первый ответ", "второй вопрос" }, provider.Calls.Last().Select(x => x.Content));
    }

    [Fact]
    public async Task ConcurrentWorkersRespectGlobalLimit()
    {
        options.MaxConcurrentRequests = 2;
        provider.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await handler.HandleAsync(Update("/chat один", 1), default);
        await handler.HandleAsync(Update("/chat два", 2, new(2, 2, true)), default);
        await handler.HandleAsync(Update("/chat три", 3, new(-100, 1, false)), default);
        var first = Worker().ProcessOneAsync(default);
        var second = Worker().ProcessOneAsync(default);
        try
        {
            await provider.StartedTwice.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(await Worker().ProcessOneAsync(default));
            Assert.Equal(2, provider.Calls.Count);
        }
        finally { provider.Block.SetResult("ответ"); await Task.WhenAll(first, second); }
        Assert.True(await Worker().ProcessOneAsync(default));
        Assert.Equal(3, provider.Calls.Count);
    }

    [Fact]
    public async Task QueuedRequestsSurviveRestartBeforeGeneration()
    {
        var update = Update("/chat сохранённый вопрос", 1);
        await handler.HandleAsync(update, default);
        Assert.Empty(provider.Calls);
        var restored = new ImageDatabase(images); restored.Migrate();
        var service = Service(restored);
        await Handler(restored, service).HandleAsync(update, default);
        Assert.Single(restored.Run(s => s.ChatRequests));
        Assert.True(await Worker(restored, service).ProcessOneAsync(default));
        Assert.Single(provider.Calls); Assert.Single(telegram.Sent);
    }

    [Fact]
    public async Task ResetCancelsQueuedOldRequestsAndNewConversationCanStartImmediately()
    {
        var blocked = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.Block = blocked;
        await handler.HandleAsync(Update("/chat старый первый", 1), default);
        await handler.HandleAsync(Update("/chat старый второй", 2), default);
        var running = Worker().ProcessOneAsync(default);
        await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await handler.HandleAsync(Update("/reset", 3), default);
            Assert.All(db.Run(s => s.ChatRequests), x => Assert.Equal(JobStatus.Cancelled, x.Status));
            provider.Block = null;
            await handler.HandleAsync(Update("/chat новый вопрос", 4), default);
            Assert.True(await Worker().ProcessOneAsync(default));
            Assert.Single(provider.Calls.Last());
            Assert.Equal("новый вопрос", provider.Calls.Last().Single().Content);
        }
        finally { blocked.SetResult("устаревший ответ"); await running; }
        Assert.Equal(2, provider.Calls.Count);
        Assert.DoesNotContain(telegram.Sent, x => x.Text == "устаревший ответ");
        Assert.Equal("новый вопрос", db.Run(s => s.Chats.Single().Turns.Single().Prompt));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task RevokedOrDisabledQueuedRequestsNeverCallProvider(bool revoke)
    {
        await handler.HandleAsync(Update("/chat вопрос", 1), default);
        if (revoke) images.AllowedUsers = []; else options.Enabled = false;
        Assert.False(await Worker().ProcessOneAsync(default));
        Assert.Equal(JobStatus.Cancelled, db.Run(s => s.ChatRequests.Single().Status));
        Assert.Empty(provider.Calls); Assert.Empty(telegram.Sent);
    }

    [Fact]
    public async Task ExpiredInFlightLeaseSendsInterruptionWithoutAnotherPaidRequest()
    {
        await handler.HandleAsync(Update("/chat вопрос", 1), default);
        db.Run(s =>
        {
            var request = s.ChatRequests.Single(); request.Status = JobStatus.Generating;
            request.LeaseOwner = "stopped-worker"; request.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
            s.Updates.Add(new(request.Id)); return true;
        });
        Assert.True(await Worker().ProcessOneAsync(default));
        Assert.Empty(provider.Calls);
        Assert.Equal(Texts.ChatInterrupted, telegram.Sent.Single().Text);
        Assert.Equal(JobStatus.Completed, db.Run(s => s.ChatRequests.Single().Status));
    }

    [Fact]
    public async Task ExpiredLeaseBeforeProviderStartedCanSafelyRunRequest()
    {
        await handler.HandleAsync(Update("/chat вопрос", 1), default);
        db.Run(s =>
        {
            var request = s.ChatRequests.Single(); request.Status = JobStatus.Generating;
            request.LeaseOwner = "stopped-worker"; request.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
            return true;
        });
        Assert.True(await Worker().ProcessOneAsync(default));
        Assert.Single(provider.Calls); Assert.Single(telegram.Sent);
    }

    [Fact]
    public async Task DeliveryBackoffKeepsConversationOrderAndDoesNotBlockOthers()
    {
        telegram.FailSend = true;
        await handler.HandleAsync(Update("/chat первый вопрос", 1), default);
        await Worker().ProcessOneAsync(default);
        telegram.FailSend = false;
        await handler.HandleAsync(Update("/chat второй вопрос", 2), default);
        await handler.HandleAsync(Update("/chat другой человек", 3, new(2, 2, true)), default);
        Assert.True(await Worker().ProcessOneAsync(default));
        Assert.Equal("другой человек", provider.Calls.Last().Single().Content);
        Assert.False(await Worker().ProcessOneAsync(default));
        db.Run(s => { s.ChatRequests.Single(x => x.Id == "update:1").NextAttemptAt = DateTimeOffset.MinValue; return true; });
        Assert.True(await Worker().ProcessOneAsync(default));
        Assert.Equal(2, provider.Calls.Count);
        Assert.True(await Worker().ProcessOneAsync(default));
        Assert.Equal("второй вопрос", provider.Calls.Last().Last().Content);
        Assert.Equal(3, provider.Calls.Last().Count);
    }

    [Fact]
    public async Task HostedWorkerActuallyRunsSeveralSlots()
    {
        provider.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        await handler.HandleAsync(Update("/chat один", 1), default);
        await handler.HandleAsync(Update("/chat два", 2, new(2, 2, true)), default);
        using var worker = Worker();
        await worker.StartAsync(default);
        try
        {
            await provider.StartedTwice.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(2, db.Run(s => s.ChatRequests.Count(x => x.Status == JobStatus.Generating)));
        }
        finally
        {
            provider.Block.SetResult("ответ");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task PollingContinuesFetchingUpdatesWhileSeveralChatResponsesAreInFlight()
    {
        provider.Block = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextPoll = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polls = 0;
        using var http = new HttpClient(new TelegramStub(async (_, ct) =>
        {
            if (Interlocked.Increment(ref polls) == 1)
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
                {
                    ok = true,
                    result = new[] { Update("/chat один", 1), Update("/chat два", 2, new(2, 2, true)) }
                })) };
            nextPoll.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("Unreachable after cancellation");
        }));
        var client = new TelegramBotClient(new TelegramBotClientOptions("123:fake") { RetryCount = 0 }, http);
        using var polling = new TelegramPollingWorker(client, new() { Username = "TestBot" }, handler, db,
            NullLogger<TelegramPollingWorker>.Instance);
        using var worker = Worker();
        await worker.StartAsync(default);
        await polling.StartAsync(default);
        try
        {
            await nextPoll.Task.WaitAsync(TimeSpan.FromSeconds(8));
            await provider.StartedTwice.Task.WaitAsync(TimeSpan.FromSeconds(8));
            Assert.Equal(2, db.Run(s => s.ChatRequests.Count(x => x.Status == JobStatus.Generating)));
            Assert.Contains(db.Run(s => s.Updates), x => x.Id == "poll:2");
            Assert.Empty(telegram.Sent);
        }
        finally
        {
            provider.Block.TrySetResult("ответ");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await polling.StopAsync(timeout.Token);
            await worker.StopAsync(timeout.Token);
        }
    }

    [Fact]
    public async Task ExpiredLeaseWithSavedAnswerRetriesOnlyDelivery()
    {
        await handler.HandleAsync(Update("/chat вопрос", 1), default);
        db.Run(s =>
        {
            var request = s.ChatRequests.Single(); request.Status = JobStatus.Generating;
            request.LeaseOwner = "stopped-worker"; request.LeaseUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
            s.Updates.Add(new(request.Id));
            s.Chats.Single().Turns.Add(new() { Key = request.Id, Prompt = request.Prompt, Answer = "сохранённый ответ" });
            return true;
        });
        Assert.True(await Worker().ProcessOneAsync(default));
        Assert.Empty(provider.Calls);
        Assert.Equal("сохранённый ответ", telegram.Sent.Single().Text);
    }

    private static JsonElement Callback(string action, Actor actor, int id) => JsonSerializer.SerializeToElement(new
    {
        update_id = id,
        callback_query = new
        {
            id = "callback-" + id, data = "ai:ui:" + action, from = new { id = actor.UserId },
            message = new { message_id = 99, chat = new { id = actor.ChatId, type = actor.IsPrivate ? "private" : "supergroup" } }
        }
    });

    private static JsonElement Update(string text, int id, Actor? actor = null, int? replyId = null)
    {
        actor ??= User;
        var message = new Dictionary<string, object>
        {
            ["message_id"] = id, ["date"] = 0, ["text"] = text, ["from"] = new { id = actor.UserId, is_bot = false },
            ["chat"] = new { id = actor.ChatId, type = actor.IsPrivate ? "private" : "supergroup" }
        };
        if (replyId != null) message["reply_to_message"] = new { message_id = replyId.Value, from = new { id = 123, is_bot = true } };
        return JsonSerializer.SerializeToElement(new { update_id = id, message });
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Directory.Delete(images.StoragePath, true);
    }
    private sealed class TelegramStub(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }
    private sealed class Provider : IChatProvider
    {
        public ConcurrentQueue<IReadOnlyList<ChatMessage>> Calls { get; } = new();
        public string Answer { get; set; } = "Ответ помощника";
        public bool Fail { get; set; }
        public string? Partial { get; set; }
        public TaskCompletionSource<string>? Block { get; set; }
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StartedTwice { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<string> ReplyAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct)
        {
            Calls.Enqueue(messages.ToArray()); Started.TrySetResult();
            if (Calls.Count >= 2) StartedTwice.TrySetResult();
            if (Fail) throw new ProviderException("test");
            return Block?.Task.WaitAsync(ct) ?? Task.FromResult(Answer);
        }
        public async Task<string> ReplyStreamingAsync(IReadOnlyList<ChatMessage> messages, Action<string> onText, CancellationToken ct)
        {
            if (Partial != null) onText(Partial);
            var answer = await ReplyAsync(messages, ct);
            onText(answer);
            return answer;
        }
    }
    private sealed class Gateway : ITelegramGateway
    {
        private readonly ConcurrentQueue<(int Id, string Text)> sent = new();
        private int nextId = 999;
        public IReadOnlyList<(int Id, string Text)> Sent => sent.ToArray();
        public ConcurrentQueue<Guid> Progress { get; } = new();
        public ConcurrentQueue<string> CallbackTexts { get; } = new();
        public TaskCompletionSource<string> TextDraft { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool FailSend { get; set; }
        public bool FailProgress { get; set; }
        public Task<int> SendTextAsync(long chatId, int messageId, string text, CancellationToken ct)
        {
            if (FailSend) throw new IOException();
            var id = Interlocked.Increment(ref nextId); sent.Enqueue((id, text)); return Task.FromResult(id);
        }
        public Task ShowChatProgressAsync(Actor actor, Guid draftId, CancellationToken ct, string? text = null)
        {
            Progress.Enqueue(draftId);
            if (!string.IsNullOrEmpty(text)) TextDraft.TrySetResult(text);
            if (FailProgress) throw new IOException();
            return Task.CompletedTask;
        }
        public Task<int> PublishAsync(ImageJob job, StoredAsset asset, Stream stream, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> DownloadAsync(InputFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task AnswerCallbackAsync(string id, string text, CancellationToken ct)
        { CallbackTexts.Enqueue(text); return Task.CompletedTask; }
    }
}
