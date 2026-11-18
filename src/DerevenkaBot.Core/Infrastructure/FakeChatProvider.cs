using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Domain;

namespace DerevenkaBot.Core.Infrastructure;

public sealed class FakeChatProvider : IChatProvider
{
    public Task<string> ReplyAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct) =>
        Task.FromResult("💬 Тестовый режим: сообщение получено. Для настоящих ответов включите OpenAI-провайдер.\n\n" + messages.Last().Content);
}
