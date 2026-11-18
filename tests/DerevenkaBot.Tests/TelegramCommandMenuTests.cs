using System.Net;
using System.Text.Json;
using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Telegram;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot;

namespace DerevenkaBot.Tests;

public sealed class TelegramCommandMenuTests
{
    [Fact]
    public async Task RegistersLocalizedPublicCommandsAndOwnerOnlyPrivateCommandsPreservingOtherModules()
    {
        var registrations = new List<JsonElement>();
        var menuButtonSet = false;
        using var http = new HttpClient(new Stub(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            if (request.RequestUri!.AbsolutePath.EndsWith("/getMyCommands"))
                return Response("[{\"command\":\"legacy\",\"description\":\"Команда другого модуля\"}]");
            if (request.RequestUri.AbsolutePath.EndsWith("/setMyCommands")) registrations.Add(body.RootElement.Clone());
            if (request.RequestUri.AbsolutePath.EndsWith("/setChatMenuButton"))
            {
                Assert.Equal("commands", body.RootElement.GetProperty("menu_button").GetProperty("type").GetString());
                menuButtonSet = true;
            }
            return Response("true");
        }));
        var menu = Menu(http, new() { Enabled = true, OwnerUserId = 777 }, new());
        await menu.SynchronizeAsync(default);
        Assert.True(menuButtonSet);
        Assert.Equal(4, registrations.Count);
        foreach (var registration in registrations)
        {
            var commands = registration.GetProperty("commands").EnumerateArray().ToArray();
            var names = commands.Select(x => x.GetProperty("command").GetString()).ToArray();
            Assert.Equal(names.Length, names.Distinct().Count());
            Assert.Contains("legacy", names);
            Assert.Contains("chat", names);
            Assert.Contains("imagine", names); Assert.Contains("edit", names); Assert.Contains("reset", names);
            Assert.Contains("usage", names); Assert.Contains("help", names); Assert.Contains("ai_id", names);
            Assert.All(commands, c => Assert.InRange(c.GetProperty("description").GetString()!.Length, 1, 256));
            var scope = registration.GetProperty("scope");
            if (scope.GetProperty("type").GetString() == "chat")
            {
                Assert.Equal(777, scope.GetProperty("chat_id").GetInt64());
                Assert.Contains("ai_allow", names); Assert.Contains("ai_revoke", names); Assert.Contains("ai_access", names);
            }
            else
            {
                Assert.Equal("default", scope.GetProperty("type").GetString());
                Assert.DoesNotContain("ai_allow", names); Assert.DoesNotContain("ai_revoke", names); Assert.DoesNotContain("ai_access", names);
            }
        }
        Assert.Equal(2, registrations.Count(x => x.GetProperty("language_code").GetString() == "ru"));
    }

    [Fact]
    public async Task PublicMenuIsRegisteredBeforeUnavailableOwnerChat()
    {
        var commandsRegistered = 0;
        var menuButtonSet = false;
        using var http = new HttpClient(new Stub(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            if (root.TryGetProperty("scope", out var scope) && scope.GetProperty("type").GetString() == "chat")
                return new(HttpStatusCode.BadRequest) { Content = new StringContent("{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: chat not found\"}") };
            if (request.RequestUri!.AbsolutePath.EndsWith("/setMyCommands")) commandsRegistered++;
            if (request.RequestUri.AbsolutePath.EndsWith("/setChatMenuButton")) menuButtonSet = true;
            return Response(request.RequestUri.AbsolutePath.EndsWith("/getMyCommands") ? "[]" : "true");
        }));
        await Assert.ThrowsAnyAsync<Exception>(() => Menu(http, new() { Enabled = true, OwnerUserId = 777 }, new()).SynchronizeAsync(default));
        Assert.Equal(2, commandsRegistered); Assert.True(menuButtonSet);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task DisabledModuleOrDisabledRegistrationMakesNoApiCalls(bool enabled, bool register)
    {
        using var http = new HttpClient(new Stub(_ => throw new InvalidOperationException("API must not be called")));
        await Menu(http, new() { Enabled = enabled }, new() { RegisterCommands = register }).SynchronizeAsync(default);
    }

    private static TelegramCommandMenu Menu(HttpClient http, ImageOptions images, TelegramOptions telegram) =>
        new(new TelegramBotClient(new TelegramBotClientOptions("123:fake") { RetryCount = 0 }, http), images, telegram,
            NullLogger<TelegramCommandMenu>.Instance);
    private static HttpResponseMessage Response(string result) => new(HttpStatusCode.OK)
    { Content = new StringContent("{\"ok\":true,\"result\":" + result + "}") };
    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
