namespace DerevenkaBot.Core.Application;

public sealed class ChatOptions
{
    public bool Enabled { get; set; } = true;
    public string Model { get; set; } = "gpt-6.1-terra";
    public int HistoryTurns { get; set; } = 12;
    public int MaxInputCharacters { get; set; } = 8000;
    public int MaxOutputTokens { get; set; } = 4096;
    public int TimeoutSeconds { get; set; } = 90;
    public int MaxConcurrentRequests { get; set; } = 4;

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Model) || HistoryTurns is < 1 or > 100 ||
            MaxInputCharacters is < 1 or > 32000 || MaxOutputTokens is < 128 or > 32768 ||
            TimeoutSeconds is < 5 or > 300 || MaxConcurrentRequests is < 1 or > 32)
            throw new InvalidOperationException("Проверьте модель и параметры Chat.");
    }
}
