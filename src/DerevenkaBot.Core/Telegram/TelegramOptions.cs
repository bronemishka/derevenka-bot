namespace DerevenkaBot.Core.Telegram;

public sealed class TelegramOptions
{
    public string Token { get; set; } = "";
    public string Username { get; set; } = "";
    public bool EnablePolling { get; set; }
    public bool RegisterCommands { get; set; } = true;
    public bool UseAiDrafts { get; set; } = true;
}
