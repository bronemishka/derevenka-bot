using System.Net;
using System.Text.Json;
using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Telegram;
using Telegram.Bot;

namespace DerevenkaBot.Tests;

public sealed class TelegramAiDraftTests
{
    [Fact]
    public async Task ChatDraftStreamsEscapedTextAndPersistsFinalRichReply()
    {
        var calls = 0;
        using var http = new HttpClient(new Stub(async request =>
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            Assert.Equal("<p>Текст &lt;b&gt;&amp;<br>Продолжение</p>", root.GetProperty("rich_message").GetProperty("html").GetString());
            calls++;
            if (calls == 1)
            {
                Assert.EndsWith("/sendRichMessageDraft", request.RequestUri!.AbsolutePath);
                return Success();
            }
            Assert.EndsWith("/sendRichMessage", request.RequestUri!.AbsolutePath);
            Assert.Equal(42, root.GetProperty("reply_parameters").GetProperty("message_id").GetInt32());
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":99,\"date\":0,\"chat\":{\"id\":1,\"type\":\"private\"}}}") };
        }));
        var gateway = Gateway(http);
        await gateway.ShowChatProgressAsync(new(1, 1, true), Guid.NewGuid(), default, "Текст <b>&\nПродолжение");
        Assert.Equal(99, await gateway.SendChatAnswerAsync(new(1, 1, true), 42, "Текст &lt;b&gt;&amp;\nПродолжение", default));
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task PrivateChatUsesThinkingWithStableDraftId()
    {
        var ids = new List<long>();
        using var http = new HttpClient(new Stub(async request =>
        {
            Assert.EndsWith("/sendRichMessageDraft", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            ids.Add(body.RootElement.GetProperty("draft_id").GetInt64());
            var html = body.RootElement.GetProperty("rich_message").GetProperty("html").GetString();
            Assert.Contains("<tg-thinking>", html); Assert.Contains("Думаю над ответом", html);
            return Success();
        }));
        var gateway = Gateway(http);
        await gateway.ShowChatProgressAsync(new(1, 1, true), Guid.Empty, default);
        await gateway.ShowChatProgressAsync(new(1, 1, true), Guid.Empty, default);
        Assert.Equal(new long[] { 1, 1 }, ids);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task GroupChatOrDisabledDraftUsesTyping(bool isPrivate, bool enabled)
    {
        using var http = new HttpClient(new Stub(async request =>
        {
            Assert.EndsWith("/sendChatAction", request.RequestUri!.AbsolutePath);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("typing", body.RootElement.GetProperty("action").GetString());
            return Success();
        }));
        await Gateway(http, enabled).ShowChatProgressAsync(new(isPrivate ? 1 : -100, 1, isPrivate), Guid.NewGuid(), default);
    }

    [Fact]
    public async Task ChatDraftUnsupportedEndpointFallsBackAndRateLimitIsRespected()
    {
        var drafts = 0; var actions = 0;
        using var http = new HttpClient(new Stub(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/sendRichMessageDraft"))
            {
                drafts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) {
                    Content = new StringContent("{\"ok\":false,\"error_code\":404,\"description\":\"Not Found\"}") });
            }
            actions++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) {
                Content = new StringContent("{\"ok\":false,\"error_code\":429,\"description\":\"Too Many Requests\",\"parameters\":{\"retry_after\":60}}") });
        }));
        var gateway = Gateway(http);
        await gateway.ShowChatProgressAsync(new(1, 1, true), Guid.NewGuid(), default);
        await gateway.ShowChatProgressAsync(new(1, 1, true), Guid.NewGuid(), default);
        Assert.Equal(1, drafts); Assert.Equal(1, actions);
    }

    [Fact]
    public async Task PrivateGenerationUsesNativeThinkingWithStableNonzeroDraftId()
    {
        var ids = new List<long>();
        using var http = new HttpClient(new Stub(async request =>
        {
            Assert.EndsWith("/sendRichMessageDraft", request.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = json.RootElement;
            ids.Add(root.GetProperty("draft_id").GetInt64());
            Assert.Contains("<tg-thinking>", root.GetProperty("rich_message").GetProperty("html").GetString());
            Assert.False(root.TryGetProperty("can_stop", out var stop) && stop.GetBoolean());
            return Success();
        }));
        var gateway = Gateway(http);
        var job = new ImageJob { Id = Guid.Empty, Actor = new(1, 1, true) };
        await gateway.ShowGenerationProgressAsync(job, default);
        await gateway.ShowGenerationProgressAsync(job, default);
        Assert.Equal(new long[] { 1, 1 }, ids);
    }
    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task GroupOrDisabledDraftsUsePhotoChatAction(bool isPrivate, bool enabled)
    {
        using var http = new HttpClient(new Stub(request =>
        {
            Assert.EndsWith("/sendChatAction", request.RequestUri!.AbsolutePath);
            return Task.FromResult(Success());
        }));
        await Gateway(http, enabled).ShowGenerationProgressAsync(new() { Actor = new(isPrivate ? 1 : -100, 1, isPrivate) }, default);
    }
    [Fact]
    public async Task UnsupportedDraftEndpointFallsBackWithoutRepeatedUnsupportedRequests()
    {
        var drafts = 0; var actions = 0;
        using var http = new HttpClient(new Stub(request =>
        {
            if (request.RequestUri!.AbsolutePath.EndsWith("/sendRichMessageDraft"))
            {
                drafts++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) {
                    Content = new StringContent("{\"ok\":false,\"error_code\":404,\"description\":\"Not Found\"}") });
            }
            Assert.EndsWith("/sendChatAction", request.RequestUri.AbsolutePath);
            actions++; return Task.FromResult(Success());
        }));
        var gateway = Gateway(http);
        var job = new ImageJob { Actor = new(1, 1, true) };
        await gateway.ShowGenerationProgressAsync(job, default);
        await gateway.ShowGenerationProgressAsync(job, default);
        Assert.Equal(1, drafts); Assert.Equal(2, actions);
    }
    [Fact]
    public async Task TelegramRetryAfterIsRespectedWithoutAffectingProviderWork()
    {
        var calls = 0;
        using var http = new HttpClient(new Stub(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests) {
                Content = new StringContent("{\"ok\":false,\"error_code\":429,\"description\":\"Too Many Requests\",\"parameters\":{\"retry_after\":60}}") });
        }));
        var gateway = Gateway(http);
        var job = new ImageJob { Actor = new(1, 1, true) };
        await gateway.ShowGenerationProgressAsync(job, default);
        await gateway.ShowGenerationProgressAsync(job, default);
        Assert.Equal(1, calls);
    }
    private static TelegramGateway Gateway(HttpClient http, bool enabled = true) => new(
        new TelegramBotClient(new TelegramBotClientOptions("123:fake") { RetryCount = 0 }, http), new ImageOptions(),
        new TelegramOptions { UseAiDrafts = enabled });
    private static HttpResponseMessage Success() => new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":true}") };
    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
