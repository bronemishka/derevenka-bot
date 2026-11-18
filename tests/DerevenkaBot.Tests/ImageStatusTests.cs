using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Infrastructure;

namespace DerevenkaBot.Tests;

public sealed class ImageStatusTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "derevenka-tests", Guid.NewGuid().ToString("N"));
    private readonly ImageDatabase db;
    private readonly FakeTelegram telegram = new();
    private readonly ImageJob job = new() { Actor = new(1, 1, true), Status = JobStatus.Generating };
    public ImageStatusTests()
    {
        db = new(new() { StoragePath = directory }); db.Migrate();
        db.Run(s => { s.Jobs.Add(job); return true; });
    }
    private ImageStatusService Status() => new(db, telegram);
    [Fact]
    public async Task NativeDraftRemovesPermanentStatusAndDoesNotCreateAdditionalProgressMessages()
    {
        await Status().AcknowledgeAsync(job.Id, 10, default);
        await Status().GeneratingAsync(job.Id, GenerationProgressMode.NativeDraft, default);
        await Status().GeneratingAsync(job.Id, GenerationProgressMode.NativeDraft, default);
        await Status().NotifyAsync(job.Id, Texts.Publishing, default);
        await Status().NotifyAsync(job.Id, Texts.Delivered, default);
        Assert.Equal(new[] { 10 }, telegram.Deleted);
        Assert.Empty(telegram.Edited); Assert.Empty(telegram.Sent);
        Assert.Null(db.Run(s => s.Jobs.Single().StatusMessageId));
    }
    [Fact]
    public async Task LateQueueAcknowledgementIsAlsoHiddenWhenNativeDraftIsAlreadyVisible()
    {
        await Status().GeneratingAsync(job.Id, GenerationProgressMode.NativeDraft, default);
        await Status().AcknowledgeAsync(job.Id, 10, default);
        Assert.Equal(new[] { 10 }, telegram.Deleted); Assert.Empty(telegram.Edited);
    }
    [Fact]
    public async Task DraftFallbackRestoresOnePermanentStatusAndDoesNotRepeatSameEdit()
    {
        await Status().AcknowledgeAsync(job.Id, 10, default);
        await Status().GeneratingAsync(job.Id, GenerationProgressMode.NativeDraft, default);
        await Status().GeneratingAsync(job.Id, GenerationProgressMode.StatusMessage, default);
        await Status().GeneratingAsync(job.Id, GenerationProgressMode.StatusMessage, default);
        Assert.Equal(new[] { Texts.Generating }, telegram.Sent);
        Assert.Empty(telegram.Edited);
        Assert.Equal(100, db.Run(s => s.Jobs.Single().StatusMessageId));
    }
    [Fact]
    public async Task RegularStatusWithoutDraftUsesOneMessageAndOnlyEditsIt()
    {
        await Status().AcknowledgeAsync(job.Id, 10, default);
        await Status().GeneratingAsync(job.Id, GenerationProgressMode.StatusMessage, default);
        await Status().GeneratingAsync(job.Id, GenerationProgressMode.StatusMessage, default);
        await Status().NotifyAsync(job.Id, Texts.Publishing, default);
        Assert.Empty(telegram.Sent); Assert.Empty(telegram.Deleted);
        Assert.Equal(new[] { Texts.Generating, Texts.Publishing }, telegram.Edited.Select(x => x.Text));
        Assert.All(telegram.Edited, x => Assert.Equal(10, x.Id));
    }
    [Fact]
    public async Task FailureAfterNativeProgressCreatesOnePersistentError()
    {
        await Status().AcknowledgeAsync(job.Id, 10, default);
        await Status().GeneratingAsync(job.Id, GenerationProgressMode.NativeDraft, default);
        await Status().NotifyAsync(job.Id, Texts.Failure, default);
        await Status().NotifyAsync(job.Id, Texts.Failure, default);
        Assert.Equal(new[] { Texts.Failure }, telegram.Sent);
        Assert.False(db.Run(s => s.Jobs.Single().NativeProgress));
    }
    [Fact]
    public async Task LateAcknowledgementDoesNotReplaceAnAlreadyPublishedError()
    {
        db.Run(s => { s.Jobs[0].Status = JobStatus.Unknown; return true; });
        await Status().NotifyAsync(job.Id, Texts.Unknown, default);
        await Status().AcknowledgeAsync(job.Id, 10, default);
        Assert.Equal(new[] { Texts.Unknown }, telegram.Sent);
        Assert.Equal(new[] { 10 }, telegram.Deleted); Assert.Empty(telegram.Edited);
        Assert.Equal(100, db.Run(s => s.Jobs.Single().StatusMessageId));
    }
    [Fact]
    public async Task ConcurrentUiUpdatesFromSeparateServiceInstancesDoNotCreateDuplicateStatuses()
    {
        await Status().AcknowledgeAsync(job.Id, 10, default);
        await Status().GeneratingAsync(job.Id, GenerationProgressMode.NativeDraft, default);
        await Task.WhenAll(Status().GeneratingAsync(job.Id, GenerationProgressMode.StatusMessage, default),
            Status().GeneratingAsync(job.Id, GenerationProgressMode.StatusMessage, default));
        Assert.Single(telegram.Sent); Assert.Empty(telegram.Edited);
    }
    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        if (Directory.Exists(directory)) Directory.Delete(directory, true);
    }
    private sealed class FakeTelegram : ITelegramGateway
    {
        public List<int> Deleted { get; } = [];
        public List<string> Sent { get; } = [];
        public List<(int Id, string Text)> Edited { get; } = [];
        public async Task<int> SendTextAsync(long chatId, int messageId, string text, CancellationToken ct)
        { await Task.Delay(5, ct); Sent.Add(text); return 100 + Sent.Count - 1; }
        public Task EditStatusAsync(long chatId, int messageId, string text, CancellationToken ct)
        { Edited.Add((messageId, text)); return Task.CompletedTask; }
        public Task DeleteStatusAsync(long chatId, int messageId, CancellationToken ct)
        { Deleted.Add(messageId); return Task.CompletedTask; }
        public Task<int> PublishAsync(ImageJob job, StoredAsset asset, Stream stream, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> DownloadAsync(InputFile file, CancellationToken ct) => throw new NotSupportedException();
        public Task AnswerCallbackAsync(string id, string text, CancellationToken ct) => Task.CompletedTask;
    }
}
