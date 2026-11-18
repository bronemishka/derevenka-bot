using DerevenkaBot.Core.Domain;

namespace DerevenkaBot.Core.Application;

public interface IChatProvider
{
    Task<string> ReplyAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct);
    async Task<string> ReplyStreamingAsync(IReadOnlyList<ChatMessage> messages, Action<string> onText, CancellationToken ct)
    {
        var answer = await ReplyAsync(messages, ct);
        onText(answer);
        return answer;
    }
}

public interface IImageGenerationProvider
{
    Task<ImageProviderResult> GenerateAsync(ImageProviderRequest request, CancellationToken ct);
    Task<ImageProviderResult> EditAsync(ImageProviderRequest request, CancellationToken ct);
}
public interface IImageAssetStore
{
    Task<StoredAsset> SaveAsync(Stream stream, string? declaredContentType, CancellationToken ct);
    Task<Stream> OpenReadAsync(Guid assetId, CancellationToken ct);
    Task DeleteAsync(Guid assetId, CancellationToken ct);
}
public interface IImageJobService
{
    Task<EnqueueResult> EnqueueAsync(ImageCommand command, CancellationToken ct);
}
public interface ITelegramGateway
{
    Task<int> SendTextAsync(long chatId, int messageId, string text, CancellationToken ct);
    Task<int> PublishAsync(ImageJob job, StoredAsset asset, Stream stream, CancellationToken ct);
    Task<Stream> DownloadAsync(InputFile file, CancellationToken ct);
    Task AnswerCallbackAsync(string id, string text, CancellationToken ct);
    Task<int> SendPanelAsync(Actor actor, int messageId, string text, BotPanel panel, bool isOwner, CancellationToken ct) =>
        SendTextAsync(actor.ChatId, messageId, text, ct);
    Task<int> RequestReplyAsync(Actor actor, int messageId, string text, CancellationToken ct) =>
        SendTextAsync(actor.ChatId, messageId, text, ct);
    Task UpdatePanelAsync(Actor actor, int messageId, string text, BotPanel panel, bool isOwner, CancellationToken ct) =>
        SendPanelAsync(actor, messageId, text, panel, isOwner, ct);
    Task<int> SendOriginalAsync(Actor actor, int messageId, Stream stream, CancellationToken ct) =>
        throw new NotSupportedException();
    Task EditStatusAsync(long chatId, int messageId, string text, CancellationToken ct) => Task.CompletedTask;
    Task ShowGeneratingAsync(long chatId, CancellationToken ct) => Task.CompletedTask;
    Task ShowTypingAsync(long chatId, CancellationToken ct) => Task.CompletedTask;
    Task ShowChatProgressAsync(Actor actor, Guid draftId, CancellationToken ct, string? text = null) => ShowTypingAsync(actor.ChatId, ct);
    Task<int> SendChatAnswerAsync(Actor actor, int messageId, string escapedText, CancellationToken ct) =>
        SendTextAsync(actor.ChatId, messageId, escapedText, ct);
    async Task<GenerationProgressMode> ShowGenerationProgressAsync(ImageJob job, CancellationToken ct)
    { await ShowGeneratingAsync(job.Actor.ChatId, ct); return GenerationProgressMode.StatusMessage; }
    Task DeleteStatusAsync(long chatId, int messageId, CancellationToken ct) => Task.CompletedTask;
    Task<int> SendAccessReviewAsync(long ownerId, AccessRequest request, CancellationToken ct) =>
        SendTextAsync(ownerId, 0, Texts.AccessRequestCard(request), ct);
    Task SendAccessDecisionAsync(AccessRequest request, CancellationToken ct) =>
        SendTextAsync(request.Applicant.ChatId, 0, Texts.AccessDecision(request), ct);
    Task UpdateAccessReviewAsync(long chatId, int messageId, AccessRequest request, CancellationToken ct) =>
        EditStatusAsync(chatId, messageId, Texts.AccessRequestCard(request), ct);
    Task SendAccessInboxAsync(Actor reviewer, int messageId, IReadOnlyList<AccessRequest> requests, CancellationToken ct) =>
        SendTextAsync(reviewer.ChatId, messageId, Texts.AccessInbox(requests), ct);
}
