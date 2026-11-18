using System.Text.Json;
using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Infrastructure;
using DerevenkaBot.Core.Telegram;
using Microsoft.Extensions.Logging.Abstractions;

namespace DerevenkaBot.Tests;

public sealed class ImageModuleTests : IDisposable
{
    private readonly ImageOptions options = new() { Enabled = true, AllowedUsers = [1, 2], SafetySalt = "test-salt-for-development",
        StoragePath = Path.Combine(Path.GetTempPath(), "derevenka-tests", Guid.NewGuid().ToString("N")) };
    private readonly ImageDatabase db;
    private readonly ImageJobService jobs;
    private readonly LocalAssetStore assets;
    private readonly FakeImageProvider provider = new();
    private readonly FakeTelegram telegram = new();
    private static readonly Actor Alice = new(-100, 1, false);
    private static readonly Actor Bob = new(-100, 2, false);
    public ImageModuleTests()
    {
        db = new(options); db.Migrate(); jobs = new(db, options); assets = new(options, db);
    }
    private ImageJobWorker Worker(IImageGenerationProvider? p = null) => new(db, options, p ?? provider, assets, telegram, NullLogger<ImageJobWorker>.Instance);
    private Task<EnqueueResult> Enqueue(string key, Actor? actor = null, bool startNew = true, int? reply = null) =>
        jobs.EnqueueAsync(new(key, actor ?? Alice, 10, "нарисуй деревню", startNew, ReplyMessageId: reply), default);
    private async Task Complete()
    { var w = Worker(); Assert.True(await w.ProcessOneAsync(default)); Assert.True(await w.ProcessOneAsync(default)); }

    [Fact]
    public async Task DuplicateUpdateCreatesOneJobAndOneProviderCall()
    {
        await Enqueue("same"); await Enqueue("same");
        await Complete();
        Assert.Equal(1, provider.Calls); Assert.Single(db.Run(s => s.Jobs)); Assert.Single(telegram.Published);
    }
    [Fact]
    public async Task SessionsAreIsolatedByChatAndUser()
    {
        await Enqueue("alice"); await Enqueue("bob", Bob); await Enqueue("private", new(1, 1, true));
        Assert.Equal(3, db.Run(s => s.Active.Select(x => x.SessionId).Distinct().Count()));
        jobs.Reset(Bob, "reset");
        Assert.Equal(2, db.Run(s => s.Active.Count(x => x.SessionId != null)));
    }
    [Fact]
    public async Task ReplyBranchesFromExactOldNodeWithoutChangingAuthorSession()
    {
        await Enqueue("first"); await Complete(); var firstMessage = telegram.Published[0];
        var firstNode = db.Run(s => s.Generations.Single().Id);
        await Enqueue("second", startNew: false); await Complete();
        var authorSession = db.Run(s => s.Active.Single().SessionId);
        await Enqueue("bob-branch", Bob, false, firstMessage);
        var branch = db.Run(s => s.Generations.Last());
        Assert.Equal(firstNode, branch.SourceGenerationId);
        Assert.Equal(db.Run(s => s.Generations.First().ResultAssetId), Assert.Single(branch.InputAssets));
        Assert.NotEqual(authorSession, branch.SessionId);
        Assert.Equal(authorSession, db.Run(s => s.Active.Single(x => x.Id == "-100:1").SessionId));
    }
    [Fact]
    public async Task ResetDuringProviderCallDoesNotActivateOldResult()
    {
        var blocked = new BlockingProvider(provider);
        await Enqueue("old");
        var oldSession = db.Run(s => s.Active.Single().SessionId);
        var processing = Worker(blocked).ProcessOneAsync(default);
        await blocked.Started.Task;
        jobs.Reset(Alice, "reset"); await Enqueue("new");
        var current = db.Run(s => s.Active.Single().SessionId);
        blocked.Continue.SetResult(); await processing;
        Assert.NotEqual(oldSession, current);
        Assert.Equal(current, db.Run(s => s.Active.Single().SessionId));
        Assert.Null(db.Run(s => s.Sessions.Single(x => x.Id == current).Head));
    }
    [Fact]
    public async Task ParallelRequestsCannotBypassDailyQuotaOrBudget()
    {
        options.LimitsEnabled = true;
        options.DailyJobsPerUser = 2; options.UserDailyBudgetUsd = .5m;
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => Enqueue("quota-" + i))));
        Assert.Equal(2, results.Count(x => x.Accepted));
        Assert.Equal(.5m, db.Run(s => s.Usage.Sum(x => x.ReservedUsd)));
    }
    [Fact]
    public async Task DisabledLimitsAcceptRequestsBeyondEveryQuotaBudgetAndQueueCap()
    {
        Assert.False(options.LimitsEnabled);
        options.DailyJobsPerUser = options.GlobalDailyJobs = options.GlobalMonthlyJobs = options.MaxPendingJobs = 1;
        options.UserDailyBudgetUsd = options.BotDailyBudgetUsd = options.BotMonthlyBudgetUsd = .01m;
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => Enqueue("unlimited-" + i))));
        Assert.All(results, result => Assert.True(result.Accepted));
        Assert.Equal(12, db.Run(s => s.Jobs.Count));
        Assert.Equal(12, db.Run(s => s.Usage.Count));
        var usage = jobs.Usage(Alice);
        Assert.Contains("отключены", usage);
        Assert.DoesNotContain("/1 заявок", usage);
        Assert.DoesNotContain("Дневной бюджет", usage);
    }
    [Fact]
    public async Task RestartRestoresQueueAndFiles()
    {
        await Enqueue("persistent");
        var restored = new ImageDatabase(options); restored.Migrate();
        Assert.Single(restored.Run(s => s.Jobs));
        await Complete();
        var assetId = restored.Run(s => s.Generations.Single().ResultAssetId!.Value);
        await using var stream = await new LocalAssetStore(options, restored).OpenReadAsync(assetId, default);
        Assert.True(stream.Length > 0);
    }
    [Fact]
    public async Task ExpiredGeneratingLeaseDoesNotMakeSecondPaidCall()
    {
        await Enqueue("unknown");
        db.Run(s => { s.Jobs[0].Status = JobStatus.Generating; s.Jobs[0].LeaseUntil = DateTimeOffset.UtcNow.AddMinutes(-1); return true; });
        Assert.False(await Worker().ProcessOneAsync(default));
        Assert.Equal(0, provider.Calls);
        Assert.Equal(JobStatus.Unknown, db.Run(s => s.Jobs.Single().Status));
        Assert.False(db.Run(s => s.Usage.Single().Released));
    }
    [Fact]
    public async Task FailedTelegramDeliveryRetriesOnlyPublication()
    {
        await Enqueue("delivery");
        var worker = Worker(); await worker.ProcessOneAsync(default);
        telegram.FailPublish = true; await worker.ProcessOneAsync(default);
        db.Run(s => { s.Jobs[0].NextAttemptAt = DateTimeOffset.UtcNow.AddSeconds(-1); return true; });
        telegram.FailPublish = false; await worker.ProcessOneAsync(default);
        Assert.Equal(1, provider.Calls); Assert.Equal(JobStatus.Completed, db.Run(s => s.Jobs.Single().Status));
    }
    [Fact]
    public async Task ConcurrentWorkersRespectUserAndGlobalLimits()
    {
        await Enqueue("alice-1"); await Enqueue("alice-2"); await Enqueue("bob", Bob);
        var blocked = new BlockingProvider(provider);
        var processing = Worker(blocked).ProcessOneAsync(default);
        await blocked.Started.Task;
        options.MaxConcurrentJobs = 1;
        Assert.False(await Worker().ProcessOneAsync(default));
        options.MaxConcurrentJobs = 2;
        Assert.True(await Worker().ProcessOneAsync(default));
        Assert.Equal(JobStatus.Queued, db.Run(s => s.Jobs.Single(x => x.IdempotencyKey == "alice-2").Status));
        Assert.Equal(JobStatus.Ready, db.Run(s => s.Jobs.Single(x => x.IdempotencyKey == "bob").Status));
        blocked.Continue.SetResult(); await processing;
    }
    [Fact]
    public void RepeatedMigrationPreservesUnrelatedTables()
    {
        using var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={Path.Combine(options.StoragePath, "images.db")}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE ExistingModule(Id INTEGER PRIMARY KEY, Value TEXT); INSERT INTO ExistingModule VALUES(1,'keep');";
        command.ExecuteNonQuery();
        db.Migrate(); db.Migrate();
        command.CommandText = "SELECT Value FROM ExistingModule WHERE Id=1";
        Assert.Equal("keep", command.ExecuteScalar());
    }
    [Fact]
    public async Task GroupChatterAndOtherBotsCommandAreIgnored()
    {
        var handler = Handler();
        Assert.False(await handler.HandleAsync(Update("привет"), default));
        Assert.False(await handler.HandleAsync(Update("/imagine@OtherBot кот"), default));
        Assert.True(await handler.HandleAsync(Update("/imagine@TestBot кот"), default));
        Assert.Single(db.Run(s => s.Jobs));
    }
    [Fact]
    public async Task StartCommandShowsHelpWithoutCreatingGeneration()
    {
        Assert.True(await Handler().HandleAsync(Update("/start", isPrivate: true), default));
        Assert.Equal(Texts.Help, Assert.Single(telegram.SentTexts));
        Assert.Empty(db.Run(s => s.Jobs));
    }
    [Theory]
    [InlineData("/ai_allow 2", false)]
    [InlineData("/ai_allow user 2", false)]
    [InlineData("/ai_allow@TestBot user 2", true)]
    public async Task AllowCommandGrantsFriendWithoutCreatingGeneration(string command, bool isPrivate)
    {
        options.OwnerUserId = 1;
        Assert.True(await Handler().HandleAsync(Update(command, isPrivate: isPrivate), default));
        var grant = Assert.Single(db.Run(s => s.AccessGrants));
        Assert.Equal("user", grant.Kind); Assert.Equal(2, grant.TargetId); Assert.True(grant.Allowed);
        Assert.Contains("Доступ выдан", Assert.Single(telegram.SentTexts));
        Assert.Empty(db.Run(s => s.Jobs));
    }
    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AllowReplyUsesVisibleOriginalAuthorAndRejectsHiddenForward(bool forwarded, bool hidden)
    {
        options.OwnerUserId = 1;
        var reply = new Dictionary<string, object>
        {
            ["message_id"] = 5, ["from"] = new { id = forwarded ? 1 : 2, is_bot = false }, ["text"] = "Привет"
        };
        if (forwarded)
            reply["forward_origin"] = hidden ? new { type = "hidden_user", sender_user_name = "Друг" } :
                (object)new { type = "user", sender_user = new { id = 2, is_bot = false } };
        var update = JsonSerializer.SerializeToElement(new { update_id = 500, message = new {
            message_id = 10, chat = new { id = -100, type = "supergroup" }, from = new { id = 1 },
            text = "/ai_allow", reply_to_message = reply } });
        Assert.True(await Handler().HandleAsync(update, default));
        if (hidden)
        {
            Assert.Empty(db.Run(s => s.AccessGrants));
            Assert.Equal(Texts.ReplyUserUnavailable, Assert.Single(telegram.SentTexts));
        }
        else
        {
            var grant = Assert.Single(db.Run(s => s.AccessGrants));
            Assert.Equal(2, grant.TargetId); Assert.True(grant.Allowed);
        }
        Assert.Empty(db.Run(s => s.Jobs));
    }
    [Fact]
    public async Task AllowCommandDoesNotPermitNonOwnerToGrantAccess()
    {
        options.OwnerUserId = 777;
        await Handler().HandleAsync(Update("/ai_allow 2"), default);
        Assert.Empty(db.Run(s => s.AccessGrants));
        Assert.Equal(Texts.OwnerRequired, Assert.Single(telegram.SentTexts));
    }
    [Fact]
    public async Task PhotoCaptionThenExplicitEditUsesLastResultAndPlainTextIsIgnored()
    {
        telegram.DownloadBytes = (await provider.GenerateAsync(new("fake", "", [], "medium", "1024x1024", null, "", Guid.NewGuid()), default)).Bytes;
        var handler = Handler();
        var photo = JsonSerializer.SerializeToElement(new { update_id = 101, message = new { message_id = 11,
            chat = new { id = 1, type = "private" }, from = new { id = 1 }, caption = "измени фон",
            photo = new[] { new { file_id = "file" } } } });
        await handler.HandleAsync(photo, default); await Complete();
        Assert.False(await handler.HandleAsync(Update("ещё ярче", 102, true), default));
        Assert.Single(db.Run(s => s.Generations));
        await handler.HandleAsync(Update("/edit ещё ярче", 103, true), default);
        Assert.All(db.Run(s => s.Generations), x => Assert.Single(x.InputAssets));
        Assert.NotNull(db.Run(s => s.Generations.Last().ParentGenerationId));
    }
    [Fact]
    public async Task ForgedPrivateCallbackDoesNotResetOtherUser()
    {
        await Enqueue("private", new(1, 1, true)); await Complete();
        var generation = db.Run(s => s.Generations.Single().Id);
        var update = JsonSerializer.SerializeToElement(new { update_id = 200, callback_query = new {
            id = "callback", data = "ai:r:" + generation.ToString("N"), from = new { id = 2 },
            message = new { message_id = telegram.Published[0], chat = new { id = 1, type = "private" } } } });
        await Handler().HandleAsync(update, default);
        Assert.Equal(Texts.Unavailable, telegram.CallbackText);
        Assert.NotNull(db.Run(s => s.Active.Single().SessionId));
    }
    [Fact]
    public async Task PublicResetButtonResetsOnlyClickerAndIsIdempotent()
    {
        await Enqueue("alice"); await Complete(); await Enqueue("bob", Bob);
        var generation = db.Run(s => s.Generations.First().Id);
        var update = JsonSerializer.SerializeToElement(new { update_id = 201, callback_query = new {
            id = "callback", data = "ai:r:" + generation.ToString("N"), from = new { id = 2 },
            message = new { message_id = telegram.Published[0], chat = new { id = -100, type = "supergroup" } } } });
        await Handler().HandleAsync(update, default);
        await Enqueue("bob-new", Bob);
        await Handler().HandleAsync(update, default);
        Assert.All(db.Run(s => s.Active), x => Assert.NotNull(x.SessionId));
    }
    [Fact]
    public async Task NonImageBytesAreRejected()
    {
        await Assert.ThrowsAnyAsync<Exception>(() => assets.SaveAsync(new MemoryStream("not an image"u8.ToArray()), "image/png", default));
        Assert.Empty(db.Run(s => s.Assets));
    }
    [Fact]
    public async Task GuessedPrivateAssetIdCannotBeUsedByAnotherUser()
    {
        await Enqueue("private", new(1, 1, true)); await Complete();
        var assetId = db.Run(s => s.Generations.Single().ResultAssetId);
        var result = await jobs.EnqueueAsync(new("stolen", Bob, 10, "измени", InputAssetId: assetId), default);
        Assert.False(result.Accepted); Assert.Equal(Texts.Unavailable, result.Text);
        Assert.Single(db.Run(s => s.Jobs));
    }
    [Theory]
    [InlineData(false, false, JobStatus.Failed, true)]
    [InlineData(false, true, JobStatus.Unknown, false)]
    [InlineData(true, false, JobStatus.Queued, false)]
    public async Task ProviderErrorsHaveSafeRetryAndReservationPolicy(bool retry, bool unknown, JobStatus status, bool released)
    {
        await Enqueue("error");
        await Worker(new ErrorProvider(retry, unknown)).ProcessOneAsync(default);
        Assert.Equal(status, db.Run(s => s.Jobs.Single().Status));
        Assert.Equal(released, db.Run(s => s.Usage.Single().Released));
    }
    private ImageUpdateHandler Handler() => new(options, new() { Username = "TestBot" }, db, jobs, assets, telegram);
    [Fact]
    public async Task PrivateAdminPromptAcceptsPlainIdAndConsumesTheStepAfterSuccess()
    {
        options.OwnerUserId = 1;
        await Handler().HandleAsync(Update("/ai_allow", 1100, true), default);
        await Handler().HandleAsync(Update("123456789", 1101, true), default);
        Assert.Equal(123456789, db.Run(s => s.AccessGrants.Single().TargetId));
        Assert.Empty(db.Run(s => s.Jobs));
        Assert.Null(new ImageConversationService(db).FindActive(new(1, 1, true)));
        Assert.False(await Handler().HandleAsync(Update("нарисуй кота", 1102, true), default));
        Assert.Empty(db.Run(s => s.Jobs));
    }
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForwardedFriendCanBeGrantedFromPrivatePromptWithoutReply(bool hidden)
    {
        options.OwnerUserId = 1;
        await Handler().HandleAsync(Update("/ai_allow", 1103, true), default);
        object origin = hidden ? new { type = "hidden_user", sender_user_name = "Друг" } :
            (object)new { type = "user", sender_user = new { id = 2, is_bot = false } };
        var forwarded = JsonSerializer.SerializeToElement(new { update_id = 1104, message = new { message_id = 1104,
            chat = new { id = 1, type = "private" }, from = new { id = 1 },
            text = "/imagine текст в пересланном сообщении", forward_origin = origin } });
        await Handler().HandleAsync(forwarded, default);
        Assert.Empty(db.Run(s => s.Jobs));
        if (hidden)
        { Assert.Empty(db.Run(s => s.AccessGrants)); Assert.Equal(Texts.ReplyUserUnavailable, telegram.SentTexts.Last()); }
        else Assert.Equal(2, db.Run(s => s.AccessGrants.Single().TargetId));
    }
    [Fact]
    public async Task InvalidAdminInputAndBareOwnerIdNeverFallThroughToGeneration()
    {
        options.OwnerUserId = 1;
        await Handler().HandleAsync(Update("/ai_allow", 1105, true), default);
        await Handler().HandleAsync(Update("@friend", 1106, true), default);
        Assert.NotNull(new ImageConversationService(db).FindActive(new(1, 1, true)));
        await Handler().HandleAsync(Update("/cancel", 1107, true), default);
        var sent = telegram.SentTexts.Count;
        Assert.False(await Handler().HandleAsync(Update("123456789", 1108, true), default));
        Assert.Equal(sent, telegram.SentTexts.Count);
        Assert.Empty(db.Run(s => s.Jobs)); Assert.Empty(db.Run(s => s.AccessGrants));
    }
    [Fact]
    public async Task PrivateContinueAcceptsPlainTextAndKeepsExactSelectedSource()
    {
        var actor = new Actor(1, 1, true);
        await Enqueue("first", actor); await Complete();
        var first = db.Run(s => s.Generations.Single().Id);
        var sourceMessage = telegram.Published[0];
        await Enqueue("second", actor); await Complete();
        await Handler().HandleAsync(Callback("ai:c:" + first.ToString("N"), actor, sourceMessage, 1109), default);
        await Handler().HandleAsync(Update("сделай вечернее освещение", 1110, true), default);
        Assert.Equal(first, db.Run(s => s.Generations.Last().SourceGenerationId));
    }
    [Fact]
    public async Task OldCancelButtonCannotCancelNewerPromptAndCurrentCancelPreservesAcceptedJobs()
    {
        var actor = new Actor(1, 1, true);
        await Enqueue("accepted", actor);
        var session = db.Run(s => s.Active.Single().SessionId);
        await Handler().HandleAsync(Callback("ai:ui:new", actor, 99, 1111), default);
        var oldPrompt = new ImageConversationService(db).FindActive(actor)!;
        await Handler().HandleAsync(Callback("ai:ui:new", actor, 99, 1112), default);
        var newPrompt = new ImageConversationService(db).FindActive(actor)!;
        await Handler().HandleAsync(Callback("ai:ui:cancel", actor, oldPrompt.MessageId, 1113), default);
        Assert.Equal(newPrompt.Id, new ImageConversationService(db).FindActive(actor)!.Id);
        await Handler().HandleAsync(Callback("ai:ui:cancel", actor, newPrompt.MessageId, 1114), default);
        Assert.Null(new ImageConversationService(db).FindActive(actor));
        Assert.Equal(session, db.Run(s => s.Active.Single().SessionId));
        Assert.Equal(JobStatus.Queued, db.Run(s => s.Jobs.Single().Status));
    }
    [Fact]
    public async Task GroupPendingPromptDoesNotCaptureUnrelatedChatter()
    {
        await Handler().HandleAsync(Callback("ai:ui:new", Alice, 99, 1115), default);
        Assert.False(await Handler().HandleAsync(Update("привет всем", 1116), default));
        Assert.Empty(db.Run(s => s.Jobs));
    }
    [Fact]
    public async Task PlainInstructionInReplyToRawPhotoWorksWithoutEditCommand()
    {
        telegram.DownloadBytes = (await provider.GenerateAsync(new("fake", "", [], "medium", "1024x1024", null, "", Guid.NewGuid()), default)).Bytes;
        var update = JsonSerializer.SerializeToElement(new { update_id = 1117, message = new { message_id = 1117,
            chat = new { id = -100, type = "supergroup" }, from = new { id = 1 }, text = "добавь снег",
            reply_to_message = new { message_id = 11, photo = new[] { new { file_id = "file" } } } } });
        await Handler().HandleAsync(update, default);
        Assert.Single(db.Run(s => s.Generations.Single().InputAssets));
        Assert.Equal("добавь снег", db.Run(s => s.Generations.Single().Prompt));
    }
    [Fact]
    public async Task EditWithoutSourceAsksForPhotoAndPhotoCaptionContinuesTheStep()
    {
        var actor = new Actor(1, 1, true);
        await Handler().HandleAsync(Callback("ai:ui:edit", actor, 99, 1118), default);
        Assert.Contains(Texts.UploadFirst, telegram.SentTexts.Last());
        telegram.DownloadBytes = (await provider.GenerateAsync(new("fake", "", [], "medium", "1024x1024", null, "", Guid.NewGuid()), default)).Bytes;
        var photo = JsonSerializer.SerializeToElement(new { update_id = 1119, message = new { message_id = 1119,
            chat = new { id = 1, type = "private" }, from = new { id = 1 }, caption = "добавь кота", photo = new[] { new { file_id = "file" } } } });
        await Handler().HandleAsync(photo, default);
        Assert.Single(db.Run(s => s.Generations.Single().InputAssets));
        Assert.Null(new ImageConversationService(db).FindActive(actor));
    }
    [Theory]
    [InlineData("/imagine", "new")]
    [InlineData("/edit", "edit")]
    [InlineData("/ai_allow", "allow")]
    [InlineData("/ai_revoke", "revoke")]
    public async Task MenuCommandWithoutArgumentsAsksForInputInsteadOfStartingAi(string command, string action)
    {
        options.OwnerUserId = 1;
        var update = Update(command, isPrivate: true);
        await Handler().HandleAsync(update, default); await Handler().HandleAsync(update, default);
        Assert.Equal(action, db.Run(s => s.Prompts.Single().Action));
        Assert.Single(telegram.SentTexts); Assert.Empty(db.Run(s => s.Jobs));
    }
    [Fact]
    public async Task PhotoWithoutCaptionRequestsInstructionAndReplyUsesSavedDraftInGroup()
    {
        telegram.DownloadBytes = (await provider.GenerateAsync(new("fake", "", [], "medium", "1024x1024", null, "", Guid.NewGuid()), default)).Bytes;
        var photo = JsonSerializer.SerializeToElement(new { update_id = 901, message = new { message_id = 11,
            chat = new { id = -100, type = "supergroup" }, from = new { id = 1 }, photo = new[] { new { file_id = "file" } } } });
        await Handler().HandleAsync(photo, default);
        Assert.Empty(db.Run(s => s.Jobs));
        var prompt = db.Run(s => s.Prompts.Single());
        var asset = db.Run(s => s.Drafts.Single().AssetId);
        await Handler().HandleAsync(PromptAnswer(Alice, prompt.MessageId, "сделай фон зимним", 902), default);
        Assert.Equal(asset, Assert.Single(db.Run(s => s.Generations.Single().InputAssets)));
    }
    [Fact]
    public async Task NewPictureButtonRequestsPromptWithoutChargingAndReplyStartsNewSession()
    {
        await Enqueue("old"); await Complete();
        var session = db.Run(s => s.Active.Single().SessionId);
        var calls = provider.Calls;
        await Handler().HandleAsync(Callback("ai:ui:new", Alice, 1000, 700), default);
        Assert.Equal(calls, provider.Calls);
        Assert.Single(db.Run(s => s.Jobs));
        var prompt = db.Run(s => s.Prompts.Single());
        await Handler().HandleAsync(PromptAnswer(Alice, prompt.MessageId, "совсем другая картинка", 701), default);
        Assert.Equal(2, db.Run(s => s.Jobs.Count));
        Assert.Empty(db.Run(s => s.Generations.Last().InputAssets));
        Assert.NotEqual(session, db.Run(s => s.Active.Single().SessionId));
    }
    [Fact]
    public async Task ContinueButtonBranchesFromExactSelectedResultForClickingUser()
    {
        await Enqueue("first"); await Complete();
        var first = db.Run(s => s.Generations.Single().Id);
        var originalMessage = telegram.Published[0];
        await Enqueue("second", startNew: false); await Complete();
        var authorSession = db.Run(s => s.Active.Single().SessionId);
        var callback = Callback("ai:c:" + first.ToString("N"), Bob, originalMessage, 702);
        await Handler().HandleAsync(callback, default); await Handler().HandleAsync(callback, default);
        Assert.Single(db.Run(s => s.Prompts));
        var prompt = db.Run(s => s.Prompts.Single());
        await Handler().HandleAsync(PromptAnswer(Bob, prompt.MessageId, "добавь снег", 703), default);
        Assert.Equal(first, db.Run(s => s.Generations.Last().SourceGenerationId));
        Assert.Equal(authorSession, db.Run(s => s.Active.Single(x => x.Id == "-100:1").SessionId));
    }
    [Fact]
    public async Task OriginalButtonReturnsSavedFileWithoutNewGenerationAndIsIdempotent()
    {
        await Enqueue("picture"); await Complete();
        var generation = db.Run(s => s.Generations.Single());
        var callback = Callback("ai:o:" + generation.Id.ToString("N"), Bob, telegram.Published[0], 704);
        await Handler().HandleAsync(callback, default); await Handler().HandleAsync(callback, default);
        Assert.Single(telegram.Originals); Assert.Equal(1, provider.Calls);
        await using var stored = await assets.OpenReadAsync(generation.ResultAssetId!.Value, default);
        using var memory = new MemoryStream(); await stored.CopyToAsync(memory);
        Assert.Equal(memory.ToArray(), telegram.Originals[0]);
        Assert.Equal(generation.Id, db.Run(s => s.Messages.Last().GenerationId));
    }
    [Theory]
    [InlineData("ai:o:")]
    [InlineData("ai:c:")]
    public async Task ResultButtonsDoNotExposePrivateImagesToAnotherUser(string action)
    {
        await Enqueue("private", new(1, 1, true)); await Complete();
        var id = db.Run(s => s.Generations.Single().Id);
        await Handler().HandleAsync(Callback(action + id.ToString("N"), new(1, 2, true), telegram.Published[0], 705), default);
        Assert.Equal(Texts.Unavailable, telegram.CallbackText);
        Assert.Empty(telegram.Originals); Assert.Empty(db.Run(s => s.Prompts));
    }
    [Fact]
    public async Task AdminButtonPromptsForFriendIdAndRechecksOwnerOnSubmission()
    {
        options.OwnerUserId = 1;
        var owner = new Actor(1, 1, true);
        await Handler().HandleAsync(Callback("ai:ui:allow", owner, 99, 706), default);
        var prompt = db.Run(s => s.Prompts.Single());
        await Handler().HandleAsync(PromptAnswer(owner, prompt.MessageId, "123456789", 707), default);
        Assert.Equal(123456789, db.Run(s => s.AccessGrants.Single().TargetId));
        Assert.Empty(db.Run(s => s.Jobs));
    }
    [Fact]
    public async Task PromptCannotBeUsedByAnotherParticipantOrAfterReset()
    {
        await Handler().HandleAsync(Callback("ai:ui:new", Alice, 99, 708), default);
        var prompt = db.Run(s => s.Prompts.Single());
        await Handler().HandleAsync(PromptAnswer(Bob, prompt.MessageId, "чужой ответ", 709), default);
        Assert.Empty(db.Run(s => s.Jobs));
        jobs.Reset(Alice, "reset-prompts");
        await Handler().HandleAsync(PromptAnswer(Alice, prompt.MessageId, "старый ответ", 710), default);
        Assert.Equal(Texts.PromptExpired, telegram.SentTexts.Last());
        Assert.Empty(db.Run(s => s.Jobs));
    }
    [Fact]
    public async Task GenerationUpdatesSingleStatusMessageInsteadOfFloodingChat()
    {
        await Handler().HandleAsync(Update("/imagine кот", isPrivate: true), default);
        Assert.Single(telegram.SentTexts);
        Assert.Equal(99, db.Run(s => s.Jobs.Single().StatusMessageId));
        await Complete();
        Assert.Single(telegram.SentTexts);
        Assert.Equal(new[] { Texts.Generating, Texts.Publishing, Texts.Delivered }, telegram.StatusEdits.Select(x => x.Text));
        Assert.All(telegram.StatusEdits, x => Assert.Equal(99, x.MessageId));
    }
    [Fact]
    public async Task OptionalStatusAndActivityFailuresDoNotLoseSuccessfulPaidResult()
    {
        telegram.FailPresentation = true;
        await Handler().HandleAsync(Update("/imagine кот", isPrivate: true), default);
        await Complete();
        Assert.Equal(JobStatus.Completed, db.Run(s => s.Jobs.Single().Status));
        Assert.Equal(1, provider.Calls); Assert.Single(telegram.Published);
    }
    [Theory]
    [InlineData("access")]
    [InlineData("allow")]
    [InlineData("revoke")]
    public async Task NonOwnerCannotUseForgedAdminNavigationCallbacks(string action)
    {
        options.OwnerUserId = 1;
        await Handler().HandleAsync(Callback("ai:ui:" + action, new(2, 2, true), 99, 711), default);
        Assert.Equal(Texts.OwnerRequired, telegram.CallbackText);
        Assert.Empty(db.Run(s => s.AccessGrants)); Assert.Empty(db.Run(s => s.Prompts));
    }
    [Fact]
    public void UsageEscapesConfiguredTextBeforeRenderingRichHtml()
    {
        options.PricingVersion = "<b>untrusted & text</b>";
        var usage = jobs.Usage(Alice);
        Assert.Contains("&lt;b&gt;untrusted &amp; text&lt;/b&gt;", usage);
        Assert.DoesNotContain(options.PricingVersion, usage);
    }
    private static JsonElement Callback(string data, Actor actor, int messageId, int updateId) => JsonSerializer.SerializeToElement(new {
        update_id = updateId, callback_query = new { id = "callback-" + updateId, data, from = new { id = actor.UserId },
            message = new { message_id = messageId, chat = new { id = actor.ChatId, type = actor.IsPrivate ? "private" : "supergroup" } } } });
    private static JsonElement PromptAnswer(Actor actor, int promptId, string text, int updateId) => JsonSerializer.SerializeToElement(new {
        update_id = updateId, message = new { message_id = updateId, text, from = new { id = actor.UserId },
            chat = new { id = actor.ChatId, type = actor.IsPrivate ? "private" : "supergroup" },
            reply_to_message = new { message_id = promptId, from = new { id = 123, is_bot = true } } } });
    private static JsonElement Update(string text, int id = 100, bool isPrivate = false) => JsonSerializer.SerializeToElement(new {
        update_id = id, message = new { message_id = 10, chat = new { id = isPrivate ? 1 : -100, type = isPrivate ? "private" : "supergroup" },
            from = new { id = 1 }, text } });
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(options.StoragePath)) Directory.Delete(options.StoragePath, true);
    }
    private sealed class BlockingProvider(FakeImageProvider inner) : IImageGenerationProvider
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Continue { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<ImageProviderResult> GenerateAsync(ImageProviderRequest request, CancellationToken ct)
        { Started.SetResult(); await Continue.Task.WaitAsync(ct); return await inner.GenerateAsync(request, ct); }
        public Task<ImageProviderResult> EditAsync(ImageProviderRequest request, CancellationToken ct) => GenerateAsync(request, ct);
    }
    private sealed class ErrorProvider(bool retry, bool unknown) : IImageGenerationProvider
    {
        public Task<ImageProviderResult> GenerateAsync(ImageProviderRequest request, CancellationToken ct) => throw new ProviderException("test", retry, unknown);
        public Task<ImageProviderResult> EditAsync(ImageProviderRequest request, CancellationToken ct) => GenerateAsync(request, ct);
    }
    private sealed class FakeTelegram : ITelegramGateway
    {
        public List<int> Published { get; } = [];
        public List<string> SentTexts { get; } = [];
        public List<(int MessageId, string Text)> StatusEdits { get; } = [];
        public List<byte[]> Originals { get; } = [];
        public bool FailPublish { get; set; }
        public bool FailPresentation { get; set; }
        public string? CallbackText { get; private set; }
        public byte[] DownloadBytes { get; set; } = [];
        public Task<int> SendTextAsync(long chatId, int messageId, string text, CancellationToken ct)
        { SentTexts.Add(text); return Task.FromResult(99); }
        public Task<int> PublishAsync(ImageJob job, StoredAsset asset, Stream stream, CancellationToken ct)
        { if (FailPublish) throw new IOException(); var id = 1000 + Published.Count; Published.Add(id); return Task.FromResult(id); }
        public Task<Stream> DownloadAsync(InputFile file, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(DownloadBytes));
        public Task AnswerCallbackAsync(string id, string text, CancellationToken ct) { CallbackText = text; return Task.CompletedTask; }
        public Task<int> RequestReplyAsync(Actor actor, int messageId, string text, CancellationToken ct)
        { SentTexts.Add(text); return Task.FromResult(2000 + SentTexts.Count); }
        public async Task<int> SendOriginalAsync(Actor actor, int messageId, Stream stream, CancellationToken ct)
        {
            using var memory = new MemoryStream(); await stream.CopyToAsync(memory, ct);
            Originals.Add(memory.ToArray()); return 3000 + Originals.Count;
        }
        public Task EditStatusAsync(long chatId, int messageId, string text, CancellationToken ct)
        { if (FailPresentation) throw new IOException(); StatusEdits.Add((messageId, text)); return Task.CompletedTask; }
        public Task ShowGeneratingAsync(long chatId, CancellationToken ct)
        { if (FailPresentation) throw new IOException(); return Task.CompletedTask; }
    }
}
