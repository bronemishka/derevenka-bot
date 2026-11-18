using System.Net;
using System.Text.Json;
using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Telegram;
using Telegram.Bot;

namespace DerevenkaBot.Tests;

public sealed class TelegramGatewayTests
{
    [Fact]
    public async Task SdkSendsMessageWithReply()
    {
        using var http = new HttpClient(new Stub(async request =>
        {
            Assert.EndsWith("/sendMessage", request.RequestUri!.AbsolutePath);
            var bodyText = await request.Content!.ReadAsStringAsync();
            using var body = JsonDocument.Parse(bodyText);
            Assert.Equal("Html", body.RootElement.GetProperty("parse_mode").GetString());
            Assert.Equal(42, body.RootElement.GetProperty("reply_parameters").GetProperty("message_id").GetInt32());
            return Success();
        }));
        var gateway = new TelegramGateway(new TelegramBotClient(new TelegramBotClientOptions("123:fake") { RetryCount = 0 }, http), new());
        Assert.Equal(17, await gateway.SendTextAsync(1, 42, "Привет", default));
    }
    [Fact]
    public async Task SdkPublishesPhotoWithCompactResetCallback()
    {
        var node = Guid.NewGuid();
        using var http = new HttpClient(new Stub(async request =>
        {
            Assert.EndsWith("/sendPhoto", request.RequestUri!.AbsolutePath);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("ai:r:" + node.ToString("N"), body);
            Assert.Contains("ai:c:" + node.ToString("N"), body);
            Assert.Contains("ai:o:" + node.ToString("N"), body);
            Assert.Contains("primary", body);
            return Success();
        }));
        var gateway = new TelegramGateway(new TelegramBotClient(new TelegramBotClientOptions("123:fake") { RetryCount = 0 }, http), new());
        await gateway.PublishAsync(new() { GenerationId = node, Actor = new(1, 1, true) },
            new(Guid.NewGuid(), "image/png", 3, "hash", 512, 512, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(1)),
            new MemoryStream([1, 2, 3]), default);
    }
    [Fact]
    public async Task RichHomePanelUsesStructuredFormattingAndOwnerButtonsOnlyInPrivate()
    {
        var sent = new List<JsonElement>();
        using var http = new HttpClient(new Stub(async request =>
        {
            Assert.EndsWith("/sendRichMessage", request.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            sent.Add(json.RootElement.Clone()); return Success();
        }));
        var gateway = new TelegramGateway(new TelegramBotClient(new TelegramBotClientOptions("123:fake") { RetryCount = 0 }, http), new());
        await gateway.SendPanelAsync(new(1, 1, true), 10, Texts.Help, BotPanel.Home, true, default);
        await gateway.SendPanelAsync(new(-100, 1, false), 10, Texts.Help, BotPanel.Home, true, default);
        Assert.Contains("<details>", sent[0].GetProperty("rich_message").GetProperty("html").GetString());
        foreach (var panel in sent)
        {
            var choices = panel.GetProperty("reply_markup").GetProperty("inline_keyboard")[0];
            Assert.Equal("💬 Чат", choices[0].GetProperty("text").GetString());
            Assert.Equal("ai:ui:chat", choices[0].GetProperty("callback_data").GetString());
            Assert.Equal("🎨 Картинки", choices[1].GetProperty("text").GetString());
            Assert.Equal("ai:ui:images", choices[1].GetProperty("callback_data").GetString());
        }
        Assert.Contains("ai:ui:access", sent[0].GetProperty("reply_markup").GetRawText());
        Assert.DoesNotContain("ai:ui:access", sent[1].GetProperty("reply_markup").GetRawText());
    }
    [Fact]
    public async Task ImagesPanelOffersCreationEditingAndReturnToMainMenu()
    {
        using var http = new HttpClient(new Stub(async request =>
        {
            Assert.EndsWith("/sendRichMessage", request.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var rows = json.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard");
            Assert.Equal("ai:ui:new", rows[0][0].GetProperty("callback_data").GetString());
            Assert.Equal("ai:ui:edit", rows[0][1].GetProperty("callback_data").GetString());
            Assert.Equal("ai:ui:help", rows[1][0].GetProperty("callback_data").GetString());
            return Success();
        }));
        var gateway = new TelegramGateway(new TelegramBotClient(new TelegramBotClientOptions("123:fake") { RetryCount = 0 }, http), new());
        await gateway.SendPanelAsync(new(1, 1, true), 10, Texts.ImagesHelp, BotPanel.Images, false, default);
    }
    [Fact]
    public async Task IdentityPanelUsesCopyButtonsAndForceReplyTargetsInitiator()
    {
        var sent = new List<JsonElement>();
        using var http = new HttpClient(new Stub(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            sent.Add(json.RootElement.Clone()); return Success();
        }));
        var gateway = new TelegramGateway(new TelegramBotClient(new TelegramBotClientOptions("123:fake") { RetryCount = 0 }, http), new());
        var actor = new Actor(-100, 77, false);
        await gateway.SendPanelAsync(actor, 10, Texts.Identity(actor), BotPanel.Identity, false, default);
        Assert.Equal("77", sent[0].GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0].GetProperty("copy_text").GetProperty("text").GetString());
        await gateway.RequestReplyAsync(actor, 10, Texts.Prompt("new", actor), default);
        var reply = sent[1].GetProperty("reply_markup");
        Assert.True(reply.GetProperty("force_reply").GetBoolean()); Assert.True(reply.GetProperty("selective").GetBoolean());
        Assert.Contains("tg://user?id=77", sent[1].GetProperty("text").GetString());
    }
    [Fact]
    public async Task CallbackToastHasNoMarkupAndFitsTelegramLimitWithoutBrokenEmoji()
    {
        using var http = new HttpClient(new Stub(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var toast = json.RootElement.GetProperty("text").GetString()!;
            Assert.InRange(toast.Length, 1, 200); Assert.DoesNotContain("<b>", toast);
            Assert.DoesNotContain('\uFFFD', toast);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"ok\":true,\"result\":true}") };
        }));
        var gateway = new TelegramGateway(new TelegramBotClient(new TelegramBotClientOptions("123:fake") { RetryCount = 0 }, http), new());
        await gateway.AnswerCallbackAsync("id", "<b>" + string.Concat(Enumerable.Repeat("🎨", 200)) + "</b>", default);
    }
    private static HttpResponseMessage Success() => new(HttpStatusCode.OK) { Content = new StringContent("""
        {"ok":true,"result":{"message_id":17,"date":1700000000,"chat":{"id":1,"type":"private"}}}
        """) };
    [Fact]
    public async Task RemovingAnAlreadyDeletedProgressMessageIsIdempotent()
    {
        using var http = new HttpClient(new Stub(request =>
        {
            Assert.EndsWith("/deleteMessage", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) {
                Content = new StringContent("{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: message to delete not found\"}") });
        }));
        var gateway = new TelegramGateway(new TelegramBotClient(new TelegramBotClientOptions("123:fake") { RetryCount = 0 }, http), new());
        await gateway.DeleteStatusAsync(1, 17, default);
    }
    [Fact]
    public async Task AccessReviewUsesOwnerPrivateChatAndSafeDecisionButtons()
    {
        var request = new AccessRequest { Applicant = new(2, 2, true), DisplayName = "Друг <script>", Username = "friend" };
        using var http = new HttpClient(new Stub(async message =>
        {
            Assert.EndsWith("/sendMessage", message.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(await message.Content!.ReadAsStringAsync());
            Assert.Equal(777, json.RootElement.GetProperty("chat_id").GetInt64());
            Assert.Contains("&lt;script&gt;", json.RootElement.GetProperty("text").GetString());
            var buttons = json.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard")[0];
            Assert.Equal("ai:ar:y:" + request.Id.ToString("N"), buttons[0].GetProperty("callback_data").GetString());
            Assert.Equal("ai:ar:n:" + request.Id.ToString("N"), buttons[1].GetProperty("callback_data").GetString());
            Assert.Equal("success", buttons[0].GetProperty("style").GetString());
            return Success();
        }));
        var gateway = new TelegramGateway(new TelegramBotClient(new TelegramBotClientOptions("123:fake") { RetryCount = 0 }, http), new());
        await gateway.SendAccessReviewAsync(777, request, default);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LockedPanelProvidesRequestButtonOrGroupDeepLink(bool isPrivate)
    {
        using var http = new HttpClient(new Stub(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var button = json.RootElement.GetProperty("reply_markup").GetProperty("inline_keyboard")[0][0];
            Assert.Equal(isPrivate ? "ai:ui:request" : "https://t.me/TestBot?start=access",
                button.GetProperty(isPrivate ? "callback_data" : "url").GetString());
            return Success();
        }));
        var gateway = new TelegramGateway(new TelegramBotClient(new TelegramBotClientOptions("123:fake") { RetryCount = 0 }, http),
            new(), new() { Username = "TestBot" });
        await gateway.SendPanelAsync(new(isPrivate ? 2 : -100, 2, isPrivate), 10, Texts.Denied, BotPanel.Locked, false, default);
    }
    [Fact]
    public async Task PrivatePromptCombinesNativeReplyWithCancelButton()
    {
        using var http = new HttpClient(new Stub(async request =>
        {
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var markup = json.RootElement.GetProperty("reply_markup");
            Assert.True(markup.GetProperty("force_reply").GetBoolean());
            Assert.Equal("ai:ui:cancel", markup.GetProperty("inline_keyboard")[0][0].GetProperty("callback_data").GetString());
            return Success();
        }));
        var gateway = new TelegramGateway(new TelegramBotClient(new TelegramBotClientOptions("123:fake") { RetryCount = 0 }, http), new());
        await gateway.RequestReplyAsync(new(1, 1, true), 10, "Что нарисовать?", default);
    }
    [Fact]
    public async Task PrivateRichNavigationEditsExistingPanelInsteadOfSendingNewMessage()
    {
        using var http = new HttpClient(new Stub(async request =>
        {
            Assert.EndsWith("/editMessageText", request.RequestUri!.AbsolutePath);
            using var json = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal(17, json.RootElement.GetProperty("message_id").GetInt32());
            Assert.False(json.RootElement.TryGetProperty("text", out _));
            Assert.Contains("<h2>", json.RootElement.GetProperty("rich_message").GetProperty("html").GetString());
            return Success();
        }));
        var gateway = new TelegramGateway(new TelegramBotClient(new TelegramBotClientOptions("123:fake") { RetryCount = 0 }, http), new());
        await gateway.UpdatePanelAsync(new(1, 1, true), 17, Texts.Help, BotPanel.Home, false, default);
    }
    [Fact]
    public async Task UnchangedPanelRefreshIsASuccessfulNoOp()
    {
        using var http = new HttpClient(new Stub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) {
            Content = new StringContent("{\"ok\":false,\"error_code\":400,\"description\":\"Bad Request: message is not modified\"}") })));
        var gateway = new TelegramGateway(new TelegramBotClient(new TelegramBotClientOptions("123:fake") { RetryCount = 0 }, http), new());
        await gateway.UpdatePanelAsync(new(1, 1, true), 17, Texts.Help, BotPanel.Home, false, default);
    }
    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
