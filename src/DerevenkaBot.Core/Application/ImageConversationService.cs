using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Infrastructure;

namespace DerevenkaBot.Core.Application;

public sealed class ImageConversationService(ImageDatabase db)
{
    public PromptReply? FindReply(long chatId, int messageId) => db.Run(s =>
        s.Prompts.Find(x => x.Owner.ChatId == chatId && x.MessageId == messageId));
    public PromptReply? FindActive(Actor actor) => db.Run(s =>
        s.Prompts.LastOrDefault(x => x.Owner == actor && x.ExpiresAt > DateTimeOffset.UtcNow));
    public void Start(PromptReply prompt) => db.Run(s =>
    {
        Expire(s, x => x.Owner == prompt.Owner);
        var index = s.Prompts.FindIndex(x => x.Id == prompt.Id);
        if (index >= 0) s.Prompts[index] = prompt; else s.Prompts.Add(prompt);
        return true;
    });
    public void Complete(PromptReply? prompt)
    {
        if (prompt != null) db.Run(s => { Expire(s, x => x.Id == prompt.Id); return true; });
    }
    public void Cancel(Actor actor) => db.Run(s =>
    {
        Expire(s, x => x.Owner == actor);
        var index = s.Drafts.FindIndex(x => x.Id == DatabaseState.ActorKey(actor));
        if (index >= 0) s.Drafts[index] = s.Drafts[index] with { ExpiresAt = DateTimeOffset.UtcNow };
        return true;
    });
    public void ClearPrompts(Actor actor) => db.Run(s => { Expire(s, x => x.Owner == actor); return true; });
    private static void Expire(DatabaseState state, Func<PromptReply, bool> predicate)
    {
        for (var i = 0; i < state.Prompts.Count; i++)
            if (predicate(state.Prompts[i])) state.Prompts[i] = state.Prompts[i] with { ExpiresAt = DateTimeOffset.UtcNow };
    }
}
