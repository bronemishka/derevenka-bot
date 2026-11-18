namespace DerevenkaBot.Core.Domain;

public sealed record Actor(long ChatId, long UserId, bool IsPrivate);
public sealed record InputFile(string FileId, string? MimeType, long? Size);
public sealed record ImageCommand(string Key, Actor Actor, int MessageId, string Prompt,
    bool StartNew = false, bool RequireImage = false, int? ReplyMessageId = null, Guid? InputAssetId = null);
public sealed record EnqueueResult(bool Accepted, string Text, Guid? JobId = null);
public enum JobStatus { Queued, Generating, Ready, Publishing, Completed, Failed, Cancelled, Unknown }
public sealed class ImageSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Actor Owner { get; set; } = new(0, 0, false);
    public bool Closed { get; set; }
    public Guid? Head { get; set; }
    public int Version { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class ActiveSession
{
    public string Id { get; set; } = "";
    public Guid? SessionId { get; set; }
}
public sealed class ImageGeneration
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public Guid? ParentGenerationId { get; set; }
    public Guid? SourceGenerationId { get; set; }
    public string Prompt { get; set; } = "";
    public string Model { get; set; } = "";
    public string Quality { get; set; } = "";
    public string Size { get; set; } = "";
    public List<Guid> InputAssets { get; set; } = [];
    public Guid? ResultAssetId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed record StoredAsset(Guid Id, string ContentType, long SizeBytes, string Sha256,
    int Width, int Height, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, Actor? Owner = null);
public sealed record TelegramMessage(string Id, long ChatId, int MessageId, Guid GenerationId, Guid AssetId, bool IsPublic);
public sealed class ImageJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IdempotencyKey { get; set; } = "";
    public Guid GenerationId { get; set; }
    public Actor Actor { get; set; } = new(0, 0, false);
    public int MessageId { get; set; }
    public int? StatusMessageId { get; set; }
    public bool StatusAcknowledged { get; set; }
    public bool NativeProgress { get; set; }
    public string? LastStatusText { get; set; }
    public int SessionVersion { get; set; }
    public JobStatus Status { get; set; }
    public int Attempts { get; set; }
    public int PublishAttempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset LeaseUntil { get; set; }
    public string? LeaseOwner { get; set; }
    public string? ErrorCode { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed class UsageRecord
{
    public Guid Id { get; set; }
    public long UserId { get; set; }
    public decimal ReservedUsd { get; set; }
    public decimal? ActualUsd { get; set; }
    public bool Released { get; set; }
    public string PricingVersion { get; set; } = "";
    public string? RawUsage { get; set; }
    public string? ProviderRequestId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
public sealed record Draft(string Id, Guid AssetId, DateTimeOffset ExpiresAt);
public sealed record PromptReply(string Id, Actor Owner, int MessageId, string Action, int? SourceMessageId, DateTimeOffset ExpiresAt);
public enum BotPanel { Home, Usage, Access, Identity, Privacy, Locked, Images }
public enum AccessRequestStatus { Pending, Approved, Declined }
public enum GenerationProgressMode { StatusMessage, NativeDraft }
public sealed class AccessRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string IdempotencyKey { get; set; } = "";
    public Actor Applicant { get; set; } = new(0, 0, true);
    public string DisplayName { get; set; } = "";
    public string? Username { get; set; }
    public AccessRequestStatus Status { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? DecidedAt { get; set; }
    public long? DecidedBy { get; set; }
    public bool OwnerNotified { get; set; }
    public int? OwnerMessageId { get; set; }
    public long? NotificationOwnerId { get; set; }
    public bool ApplicantNotified { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public string? LeaseOwner { get; set; }
    public DateTimeOffset LeaseUntil { get; set; }
}
public sealed record AccessRequestResult(AccessRequest? Request, string Text, bool Changed = false);
public sealed record ProviderAsset(byte[] Bytes, string ContentType);
public sealed record ImageProviderRequest(string Model, string Prompt, IReadOnlyList<ProviderAsset> InputAssets,
    string Quality, string Size, string? Complexity, string SafetyIdentifier, Guid CorrelationId);
public sealed record ImageProviderResult(byte[] Bytes, string ContentType, string Model,
    string? RequestId = null, string? UsageJson = null, decimal? ActualUsd = null);
public sealed class ProviderException(string code, bool retryable = false, bool uncertain = false) : Exception(code)
{
    public string Code { get; } = code;
    public bool Retryable { get; } = retryable;
    public bool Uncertain { get; } = uncertain;
}
