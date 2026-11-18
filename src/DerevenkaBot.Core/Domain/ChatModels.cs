namespace DerevenkaBot.Core.Domain;

public sealed record ChatMessage(string Role, string Content);

public sealed class ChatConversation
{
    public string Id { get; set; } = "";
    public int Version { get; set; }
    public List<ChatTurn> Turns { get; set; } = [];
}

public sealed class ChatTurn
{
    public string Key { get; set; } = "";
    public string Prompt { get; set; } = "";
    public string Answer { get; set; } = "";
    public bool Failed { get; set; }
    public List<int> MessageIds { get; set; } = [];
}

public sealed class ChatRequest
{
    public string Id { get; set; } = "";
    public long Sequence { get; set; }
    public Actor Actor { get; set; } = new(0, 0, true);
    public int MessageId { get; set; }
    public string Prompt { get; set; } = "";
    public int ConversationVersion { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Queued;
    public string? LeaseOwner { get; set; }
    public DateTimeOffset LeaseUntil { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public int DeliveryFailures { get; set; }
}
