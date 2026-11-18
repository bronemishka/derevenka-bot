using System.Text.Json;
using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Infrastructure;
using DerevenkaBot.Core.Telegram;
using Microsoft.Extensions.Logging.Abstractions;

namespace DerevenkaBot.Tests;

public sealed class AccessRequestTests : IDisposable
{
    private readonly ImageOptions options = new() { Enabled = true, OwnerUserId = 777,
        StoragePath = Path.Combine(Path.GetTempPath(), "derevenka-tests", Guid.NewGuid().ToString("N")) };
    private readonly ImageDatabase db;
    private readonly AccessRequestService requests;
    private readonly FakeTelegram telegram = new();
    private static readonly Actor User = new(2, 2, true);
    private static readonly Actor Owner = new(777, 777, true);
    public AccessRequestTests() { db = new(options); db.Migrate(); requests = new(db, options); }
    private AccessRequest Submit(string key = "submit") => requests.Submit(User, "Друг <b>&</b>", "friend", key).Request!;
    private AccessRequestWorker Worker() => new(db, options, telegram, NullLogger<AccessRequestWorker>.Instance);
    private bool Allowed() => new ImageAccessService(db, options).IsAllowed(User);
    private void RetryNow() => db.Run(s => { foreach (var r in s.AccessRequests) r.NextAttemptAt = DateTimeOffset.MinValue; return true; });

    [Fact]
    public async Task ParallelRepeatedRequestsCreateOnePendingApplication()
    {
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => Task.Run(() => requests.Submit(User, "Друг", null, "submit-" + i))));
        Assert.Single(db.Run(s => s.AccessRequests)); Assert.Equal(1, results.Count(x => x.Changed));
        Assert.False(Allowed()); Assert.Empty(db.Run(s => s.Jobs));
    }
    [Fact]
    public void ApprovalPersistsPersonalPermissionAfterRestart()
    {
        var request = Submit();
        Assert.True(requests.Decide(Owner, request.Id, true).Changed);
        var restored = new ImageDatabase(options); restored.Migrate();
        Assert.True(new ImageAccessService(restored, options).IsAllowed(User));
        Assert.Equal(AccessRequestStatus.Approved, restored.Run(s => s.AccessRequests.Single().Status));
    }
    [Fact]
    public void DecliningDoesNotGrantOrRevokeIndependentPermission()
    {
        var request = Submit();
        options.AllowedUsers = [2];
        requests.Decide(Owner, request.Id, false);
        Assert.Empty(db.Run(s => s.AccessGrants)); Assert.True(Allowed());
        Assert.Equal(AccessRequestStatus.Declined, db.Run(s => s.AccessRequests.Single().Status));
    }
    [Theory]
    [InlineData(2, 2, true)]
    [InlineData(-100, 777, false)]
    [InlineData(123, 777, true)]
    public void OnlyOwnerInOwnPrivateChatCanReview(long chatId, long userId, bool isPrivate)
    {
        var request = Submit();
        Assert.False(requests.Decide(new(chatId, userId, isPrivate), request.Id, true).Changed);
        Assert.False(Allowed()); Assert.Empty(db.Run(s => s.AccessGrants));
    }
    [Fact]
    public void OldApprovedButtonCannotRestoreManuallyRevokedPermission()
    {
        var request = Submit(); requests.Decide(Owner, request.Id, true);
        new ImageAccessService(db, options).Manage(Owner, "/revoke", "user 2", "manual-revoke");
        Assert.False(requests.Decide(Owner, request.Id, true).Changed); Assert.False(Allowed());
    }
    [Fact]
    public void OldDeclinedButtonDoesNotDecideNewRequest()
    {
        var old = Submit(); requests.Decide(Owner, old.Id, false);
        var current = Submit("resubmit");
        Assert.NotEqual(old.Id, current.Id);
        Assert.False(requests.Decide(Owner, old.Id, true).Changed);
        Assert.Equal(AccessRequestStatus.Pending, db.Run(s => s.AccessRequests.Single(x => x.Id == current.Id).Status));
        Assert.False(Allowed());
    }
    [Fact]
    public async Task ConcurrentApproveAndDeclineHaveOneWinner()
    {
        var request = Submit();
        var decisions = await Task.WhenAll(Task.Run(() => requests.Decide(Owner, request.Id, true)),
            Task.Run(() => requests.Decide(Owner, request.Id, false)));
        Assert.Equal(1, decisions.Count(x => x.Changed));
        Assert.Equal(db.Run(s => s.AccessRequests.Single().Status) == AccessRequestStatus.Approved, Allowed());
    }
    [Fact]
    public void GroupRequestsAndAlreadyAuthorizedUsersDoNotCreateApplications()
    {
        Assert.Null(requests.Submit(new(-100, 2, false), "Друг", null, "group").Request);
        Assert.Null(requests.Submit(Owner, "Владелец", null, "owner").Request);
        Assert.Empty(db.Run(s => s.AccessRequests));
    }
    [Fact]
    public void ManualUserGrantAlsoCompletesPendingApplication()
    {
        Submit(); new ImageAccessService(db, options).Manage(Owner, "/allow", "user 2", "manual");
        Assert.Equal(AccessRequestStatus.Approved, db.Run(s => s.AccessRequests.Single().Status)); Assert.True(Allowed());
    }
    [Fact]
    public async Task NotificationsRecoverAfterFailureAndRestartWithoutRegranting()
    {
        var request = Submit(); telegram.FailReview = true;
        await Worker().ProcessOneAsync(default);
        Assert.False(db.Run(s => s.AccessRequests.Single().OwnerNotified));
        telegram.FailReview = false; RetryNow();
        await Worker().ProcessOneAsync(default);
        Assert.Single(telegram.Reviews);
        Assert.False(await Worker().ProcessOneAsync(default));
        requests.Decide(Owner, request.Id, true);
        telegram.FailDecision = true; await Worker().ProcessOneAsync(default);
        Assert.True(Allowed()); Assert.False(db.Run(s => s.AccessRequests.Single().ApplicantNotified));
        telegram.FailDecision = false; RetryNow();
        await new AccessRequestWorker(new ImageDatabase(options), options, telegram, NullLogger<AccessRequestWorker>.Instance).ProcessOneAsync(default);
        Assert.Single(telegram.Decisions); Assert.Single(db.Run(s => s.AccessGrants));
        Assert.True(db.Run(s => s.AccessRequests.Single().ApplicantNotified));
    }
    [Fact]
    public async Task DeniedStartThenButtonThenOwnerDecisionWorksThroughUpdateHandler()
    {
        var handler = new ImageUpdateHandler(options, new() { Username = "TestBot" }, db, new(db, options), new LocalAssetStore(options, db), telegram);
        await handler.HandleAsync(Message("/start", User, 1), default);
        Assert.Equal(BotPanel.Locked, telegram.Panels.Last());
        await handler.HandleAsync(Callback("ai:ui:request", User, 2), default);
        var request = db.Run(s => s.AccessRequests.Single());
        await Worker().ProcessOneAsync(default);
        await handler.HandleAsync(Callback("ai:ar:y:" + request.Id.ToString("N"), User, 3), default);
        Assert.False(Allowed());
        await handler.HandleAsync(Callback("ai:ar:y:" + request.Id.ToString("N"), Owner, 4), default);
        Assert.True(Allowed());
        await Worker().ProcessOneAsync(default);
        Assert.Single(telegram.Decisions); Assert.Empty(db.Run(s => s.Jobs));
    }
    [Fact]
    public void RequestProfileIsEscapedForHtml()
    {
        var request = Submit(); var text = Texts.AccessRequestCard(request);
        Assert.Contains("Друг &lt;b&gt;&amp;&lt;/b&gt;", text);
        Assert.DoesNotContain("Друг <b>", text);
    }
    private static JsonElement Message(string text, Actor actor, int id) => JsonSerializer.SerializeToElement(new { update_id = id,
        message = new { message_id = id, text, chat = new { id = actor.ChatId, type = "private" }, from = new { id = actor.UserId, first_name = "Друг" } } });
    private static JsonElement Callback(string data, Actor actor, int id) => JsonSerializer.SerializeToElement(new { update_id = id,
        callback_query = new { id = "callback-" + id, data, from = new { id = actor.UserId, first_name = "Друг" },
            message = new { message_id = 10, chat = new { id = actor.ChatId, type = "private" } } } });
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(options.StoragePath)) Directory.Delete(options.StoragePath, true);
    }
    private sealed class FakeTelegram : ITelegramGateway
    {
        public List<AccessRequest> Reviews { get; } = [];
        public List<AccessRequest> Decisions { get; } = [];
        public List<BotPanel> Panels { get; } = [];
        public bool FailReview { get; set; }
        public bool FailDecision { get; set; }
        public Task<int> SendTextAsync(long chatId, int messageId, string text, CancellationToken ct) => Task.FromResult(10);
        public Task<int> PublishAsync(ImageJob job, StoredAsset asset, Stream stream, CancellationToken ct) => throw new InvalidOperationException("No images expected");
        public Task<Stream> DownloadAsync(InputFile file, CancellationToken ct) => throw new InvalidOperationException("No files expected");
        public Task AnswerCallbackAsync(string id, string text, CancellationToken ct) => Task.CompletedTask;
        public Task<int> SendPanelAsync(Actor actor, int messageId, string text, BotPanel panel, bool isOwner, CancellationToken ct)
        { Panels.Add(panel); return Task.FromResult(10); }
        public Task<int> SendAccessReviewAsync(long ownerId, AccessRequest request, CancellationToken ct)
        { if (FailReview) throw new IOException(); Reviews.Add(request); return Task.FromResult(10); }
        public Task SendAccessDecisionAsync(AccessRequest request, CancellationToken ct)
        { if (FailDecision) throw new IOException(); Decisions.Add(request); return Task.CompletedTask; }
    }
}
