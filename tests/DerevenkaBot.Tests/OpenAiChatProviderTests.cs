using System.Net;
using System.Text.Json;
using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Infrastructure;

namespace DerevenkaBot.Tests;

public sealed class OpenAiChatProviderTests
{
    [Fact]
    public async Task StreamsTextBeforeHttpBodyCompletes()
    {
        var head = "event: response.output_text.delta\ndata: {\"type\":\"response.output_text.delta\",\"delta\":\"Привет\"}\n\n";
        var tail = "data: {\"type\":\"response.output_text.delta\",\"delta\":\", мир!\"}\n\ndata: {\"type\":\"response.completed\"}\n\n";
        using var stream = new GatedStream(head, tail);
        using var http = new HttpClient(new Stub(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StreamContent(stream) }))) { BaseAddress = new("https://api.openai.com/v1/") };
        var received = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new OpenAiChatProvider(http, new(), new());
        var task = provider.ReplyStreamingAsync([new("user", "вопрос")], text => received.TrySetResult(text), default);
        Assert.Equal("Привет", await received.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.False(task.IsCompleted);
        stream.Release.SetResult();
        Assert.Equal("Привет, мир!", await task);
    }

    [Theory]
    [InlineData("data: {\"type\":\"response.output_text.delta\",\"delta\":\"partial\"}\n\n")]
    [InlineData("data: {\"type\":\"response.failed\"}\n\n")]
    [InlineData("data: {\"type\":\"response.incomplete\"}\n\n")]
    [InlineData("data: {\"type\":\"error\"}\n\n")]
    [InlineData("data: invalid\n\n")]
    public async Task InterruptedOrFailedStreamIsNotTreatedAsCompletedAnswer(string data)
    {
        var calls = 0;
        using var http = new HttpClient(new Stub(_ =>
        {
            calls++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(data, System.Text.Encoding.UTF8, "text/event-stream") });
        })) { BaseAddress = new("https://api.openai.com/v1/") };
        await Assert.ThrowsAsync<ProviderException>(() => new OpenAiChatProvider(http, new(), new()).ReplyAsync([new("user", "вопрос")], default));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SendsResponsesRequestWithHistoryAndReadsTextAfterReasoning()
    {
        using var http = new HttpClient(new Stub(async request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("https://api.openai.com/v1/responses", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            Assert.Equal("fake-key", request.Headers.Authorization.Parameter);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            var root = body.RootElement;
            Assert.Equal("test-text-model", root.GetProperty("model").GetString());
            Assert.False(root.GetProperty("store").GetBoolean());
            Assert.True(root.GetProperty("stream").GetBoolean());
            Assert.Equal(4096, root.GetProperty("max_output_tokens").GetInt32());
            var input = root.GetProperty("input").EnumerateArray().ToArray();
            Assert.Equal(new[] { "user", "assistant", "user" }, input.Select(x => x.GetProperty("role").GetString()));
            Assert.Equal("мой вопрос", input.Last().GetProperty("content").GetString());
            return Response("""
                {"output":[{"type":"reasoning","summary":[]},{"type":"message","role":"assistant","content":[
                  {"type":"output_text","text":"Первая часть"},{"type":"output_text","text":"Вторая часть"}]}]}
                """);
        })) { BaseAddress = new("https://api.openai.com/v1/") };
        var provider = new OpenAiChatProvider(http, new() { ApiKey = "fake-key" }, new() { Model = "test-text-model" });
        var answer = await provider.ReplyAsync([new("user", "привет"), new("assistant", "здравствуй"), new("user", "мой вопрос")], default);
        Assert.Equal("Первая часть\n\nВторая часть", answer);
    }

    [Theory]
    [InlineData(401)]
    [InlineData(429)]
    [InlineData(500)]
    public async Task HttpErrorsNeverTriggerAutomaticPaidRetries(int status)
    {
        var calls = 0;
        using var http = new HttpClient(new Stub(_ =>
        {
            calls++; return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status));
        })) { BaseAddress = new("https://api.openai.com/v1/") };
        await Assert.ThrowsAsync<ProviderException>(() => new OpenAiChatProvider(http, new(), new()).ReplyAsync([new("user", "вопрос")], default));
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("{\"output\":[]}")]
    [InlineData("{\"error\":{\"message\":\"failed\"},\"output\":[]}")]
    public async Task EmptyOrFailedResponsesProduceControlledError(string response)
    {
        using var http = new HttpClient(new Stub(_ => Task.FromResult(Response(response)))) { BaseAddress = new("https://api.openai.com/v1/") };
        await Assert.ThrowsAsync<ProviderException>(() => new OpenAiChatProvider(http, new(), new()).ReplyAsync([new("user", "вопрос")], default));
    }

    [Fact]
    public async Task RefusalIsReturnedAsReadableAnswer()
    {
        using var http = new HttpClient(new Stub(_ => Task.FromResult(Response("""
            {"output":[{"type":"message","content":[{"type":"refusal","refusal":"Не могу помочь с этим запросом."}]}]}
            """)))) { BaseAddress = new("https://api.openai.com/v1/") };
        Assert.Equal("Не могу помочь с этим запросом.",
            await new OpenAiChatProvider(http, new(), new()).ReplyAsync([new("user", "вопрос")], default));
    }

    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
        { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    private sealed class Stub(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
    private sealed class GatedStream(string head, string tail) : Stream
    {
        private readonly MemoryStream first = new(System.Text.Encoding.UTF8.GetBytes(head));
        private readonly MemoryStream second = new(System.Text.Encoding.UTF8.GetBytes(tail));
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (first.Position < first.Length) return await first.ReadAsync(buffer, cancellationToken);
            await Release.Task.WaitAsync(cancellationToken);
            return await second.ReadAsync(buffer, cancellationToken);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        { if (disposing) { first.Dispose(); second.Dispose(); } base.Dispose(disposing); }
    }
}
