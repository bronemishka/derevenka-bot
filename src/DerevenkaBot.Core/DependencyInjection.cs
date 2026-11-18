using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Infrastructure;
using DerevenkaBot.Core.Telegram;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Telegram.Bot;

namespace DerevenkaBot.Core;

public static class DependencyInjection
{
    public static IServiceCollection AddImageGeneration(this IServiceCollection services, ImageOptions options, TelegramOptions telegram,
        ChatOptions? chat = null)
    {
        chat ??= new();
        chat.Validate();
        options.Validate();
        SafetySaltStore.Initialize(options);
        services.AddSingleton(options).AddSingleton(telegram);
        services.AddSingleton(chat);
        services.AddSingleton<ChatService>();
        services.AddSingleton<IChatProvider>(_ => options.UseFakeProvider ? new FakeChatProvider() :
            new OpenAiChatProvider(new HttpClient { BaseAddress = new Uri("https://api.openai.com/v1/"),
                Timeout = TimeSpan.FromSeconds(chat.TimeoutSeconds) }, options, chat));
        services.AddSingleton<ImageDatabase>();
        services.AddSingleton<IImageAssetStore, LocalAssetStore>();
        services.AddSingleton<ImageJobService>();
        services.AddSingleton<ImageAccessService>();
        services.AddSingleton<AccessRequestService>();
        services.AddSingleton<IImageJobService>(sp => sp.GetRequiredService<ImageJobService>());
        services.AddSingleton<ImageUpdateHandler>();
        services.TryAddSingleton<ITelegramBotClient>(_ => new TelegramBotClient(
            new TelegramBotClientOptions(telegram.Token) { RetryCount = 0 }, new HttpClient { Timeout = TimeSpan.FromSeconds(60) }));
        services.AddSingleton<TelegramGateway>();
        services.AddSingleton<ITelegramGateway>(sp => sp.GetRequiredService<TelegramGateway>());
        services.AddSingleton<IImageGenerationProvider>(_ => options.UseFakeProvider ? new FakeImageProvider() :
            new OpenAiImageProvider(new HttpClient { BaseAddress = new Uri("https://api.openai.com/v1/"), Timeout = TimeSpan.FromMinutes(5) }, options));
        services.AddHostedService<TelegramCommandMenu>();
        services.AddHostedService<AccessRequestWorker>();
        services.AddHostedService<ChatJobWorker>();
        services.AddHostedService<ImageJobWorker>();
        if (telegram.EnablePolling) services.AddHostedService<TelegramPollingWorker>();
        return services;
    }
}
