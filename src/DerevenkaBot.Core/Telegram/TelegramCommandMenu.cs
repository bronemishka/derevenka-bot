using DerevenkaBot.Core.Application;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types;

namespace DerevenkaBot.Core.Telegram;

public sealed class TelegramCommandMenu(ITelegramBotClient client, ImageOptions images, TelegramOptions telegram,
    ILogger<TelegramCommandMenu> logger) : BackgroundService
{
    private static BotCommand[] PublicCommands =>
    [
        new() { Command = "chat", Description = "Задать вопрос — продолжай ответом на сообщение бота" },
        new() { Command = "imagine", Description = "Создать изображение — добавь описание" },
        new() { Command = "edit", Description = "Изменить фото — добавь инструкцию" },
        new() { Command = "reset", Description = "Начать разговор и картинки заново" },
        new() { Command = "cancel", Description = "Отменить ввод описания или ID" },
        new() { Command = "usage", Description = "Посмотреть количество запросов и расходы" },
        new() { Command = "help", Description = "Открыть главное меню" },
        new() { Command = "ai_id", Description = "Узнать свой Telegram ID и ID чата" },
        new() { Command = "request_access", Description = "Отправить владельцу заявку на доступ" }
    ];
    private static BotCommand[] OwnerCommands =>
    [
        new() { Command = "ai_allow", Description = "Выдать доступ: ID друга или ответ на его сообщение" },
        new() { Command = "ai_revoke", Description = "Отозвать доступ: ID друга или ответ на его сообщение" },
        new() { Command = "ai_access", Description = "Посмотреть выданные разрешения" },
        new() { Command = "ai_requests", Description = "Рассмотреть заявки на доступ" }
    ];

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!images.Enabled || !telegram.RegisterCommands) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await SynchronizeAsync(stoppingToken);
                logger.LogInformation("Telegram command menu registered");
                return;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                // Telegram errors may contain the token-bearing URL; log only the exception type.
                logger.LogWarning("Cannot register Telegram commands: {Type}. Retrying in 30 seconds", ex.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }

    public async Task SynchronizeAsync(CancellationToken ct)
    {
        if (!images.Enabled || !telegram.RegisterCommands) return;
        // Register a fallback for every client language and an explicit Russian localization.
        var commonMenus = new Dictionary<string, BotCommand[]>();
        foreach (var language in new[] { "", "ru" })
        {
            var scope = new BotCommandScopeDefault();
            var existing = await client.GetMyCommands(scope, language, ct);
            var commands = Merge(existing, PublicCommands);
            await client.SetMyCommands(commands, scope, language, ct);
            commonMenus[language] = commands;
        }
        await client.SetChatMenuButton(menuButton: new MenuButtonCommands(), cancellationToken: ct);
        if (images.OwnerUserId > 0)
        {
            foreach (var language in new[] { "", "ru" })
            {
                // A private chat ID equals its user's ID. This scope does not expose admin commands to groups.
                var ownerScope = new BotCommandScopeChat { ChatId = images.OwnerUserId };
                var ownerExisting = await client.GetMyCommands(ownerScope, language, ct);
                await client.SetMyCommands(Merge(ownerExisting, commonMenus[language].Concat(OwnerCommands)), ownerScope, language, ct);
            }
        }
    }

    private static BotCommand[] Merge(IEnumerable<BotCommand> existing, IEnumerable<BotCommand> commands) =>
        existing.Concat(commands).GroupBy(x => x.Command, StringComparer.Ordinal).Select(x => x.Last()).ToArray();
}
