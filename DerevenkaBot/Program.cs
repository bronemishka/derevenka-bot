using DerevenkaBot.Core;
using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Infrastructure;
using DerevenkaBot.Core.Telegram;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var builder = Host.CreateApplicationBuilder(args);
builder.Configuration.AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"), optional: true)
    .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json"), optional: true)
    .AddJsonFile("appsettings.Local.json", optional: true).AddUserSecrets<Program>(optional: true).AddEnvironmentVariables().AddCommandLine(args);
var options = builder.Configuration.GetSection("ImageGeneration").Get<ImageOptions>() ?? new();
var telegram = builder.Configuration.GetSection("Telegram").Get<TelegramOptions>() ?? new();
var chat = builder.Configuration.GetSection("Chat").Get<ChatOptions>() ?? new();
options.ApiKey = builder.Configuration["OPENAI_API_KEY"] ?? options.ApiKey;
telegram.Token = builder.Configuration["TELEGRAM_BOT_TOKEN"] ?? telegram.Token;
if (args.Contains("--migrate", StringComparer.Ordinal))
{
    new ImageDatabase(options).Migrate();
    Console.WriteLine("SQLite: миграции применены."); return;
}
if (!options.Enabled || !options.UseFakeProvider && string.IsNullOrWhiteSpace(options.ApiKey))
{
    Console.WriteLine("Модуль изображений отключён: Enabled=false или отсутствует OPENAI_API_KEY."); return;
}
if (string.IsNullOrWhiteSpace(telegram.Token))
{
    Console.WriteLine("Модуль изображений отключён: отсутствует TELEGRAM_BOT_TOKEN."); return;
}
try { builder.Services.AddImageGeneration(options, telegram, chat); }
catch (InvalidOperationException ex) { Console.WriteLine(ex.Message); return; }
using var host = builder.Build();
host.Services.GetRequiredService<ImageDatabase>().Migrate();
Console.WriteLine(telegram.EnablePolling
    ? "Telegram polling включён: бот получает команды и сообщения."
    : "Telegram polling выключен (Telegram:EnablePolling=false): этот хост не получает команды. Для обычного запуска бота включите EnablePolling; для интеграции передавайте Update из внешнего обработчика.");
await host.RunAsync();
