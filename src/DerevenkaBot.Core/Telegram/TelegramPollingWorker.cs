using DerevenkaBot.Core.Infrastructure;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Telegram.Bot;
using Telegram.Bot.Types.Enums;

namespace DerevenkaBot.Core.Telegram;

public sealed class TelegramPollingWorker(ITelegramBotClient client, TelegramOptions options, ImageUpdateHandler handler,
    ImageDatabase db, ILogger<TelegramPollingWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.Username.Length == 0)
                    options.Username = (await client.GetMe(stoppingToken)).Username!;
                var offset = db.Run(s => s.Updates.Where(x => x.Id.StartsWith("poll:"))
                    .Select(x => long.Parse(x.Id[5..], System.Globalization.CultureInfo.InvariantCulture)).DefaultIfEmpty(-1).Max() + 1);
                var updates = await client.GetUpdates(offset: checked((int)offset), timeout: 25,
                    allowedUpdates: [UpdateType.Message, UpdateType.CallbackQuery], cancellationToken: stoppingToken);
                foreach (var update in updates)
                {
                    await handler.HandleAsync(update, stoppingToken);
                    var id = update.Id;
                    db.Run(s => { s.Updates.Add(new("poll:" + id)); return true; });
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                logger.LogWarning("Telegram polling failed: {Type}", ex.GetType().Name);
                await Task.Delay(3000, stoppingToken);
            }
        }
    }
}
