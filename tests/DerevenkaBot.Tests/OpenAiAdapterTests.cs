using System.Net;
using System.Text;
using System.Text.Json;
using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Domain;
using DerevenkaBot.Core.Infrastructure;

namespace DerevenkaBot.Tests;

public sealed class OpenAiAdapterTests
{
    private static ImageProviderRequest Request(bool edit = false) => new("configured-model", "измени только фон",
        edit ? [new([1, 2, 3], "image/png"), new([4, 5], "image/png")] : [], "medium", "1024x1024", null, "hash", Guid.NewGuid());
    [Fact]
    public async Task GenerationUsesConfiguredFieldsAndDecodesBinaryAndUsage()
    {
        using var http = Client(async request =>
        {
            Assert.EndsWith("/v1/images/generations", request.RequestUri!.AbsoluteUri);
            Assert.Equal("Bearer", request.Headers.Authorization!.Scheme);
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync());
            Assert.Equal("configured-model", body.RootElement.GetProperty("model").GetString());
            Assert.Equal("измени только фон", body.RootElement.GetProperty("prompt").GetString());
            Assert.Equal("hash", body.RootElement.GetProperty("safety_identifier").GetString());
            return new(HttpStatusCode.OK) { Content = new StringContent("""
                {"data":[{"b64_json":"AQID"}],"usage":{"input_tokens_details":{"text_tokens":100,"image_tokens":200},"output_tokens":300}}
                """) };
        });
        var result = await new OpenAiImageProvider(http, new() { ApiKey = "fake", InputTextUsdPerMillion = 1,
            InputImageUsdPerMillion = 2, OutputImageUsdPerMillion = 3 }).GenerateAsync(Request(), default);
        Assert.Equal(new byte[] { 1, 2, 3 }, result.Bytes);
        Assert.Equal(.0014m, result.ActualUsd);
    }
    [Fact]
    public async Task EditSendsAllReferencesAndOriginalInstruction()
    {
        using var http = Client(async request =>
        {
            Assert.EndsWith("/v1/images/edits", request.RequestUri!.AbsoluteUri);
            var form = Assert.IsType<MultipartFormDataContent>(request.Content);
            Assert.Equal(2, form.Count(x => x.Headers.ContentDisposition!.Name!.Trim('"') == "image[]"));
            var prompt = form.Single(x => x.Headers.ContentDisposition!.Name!.Trim('"') == "prompt");
            Assert.Equal("измени только фон", await prompt.ReadAsStringAsync());
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"data\":[{\"b64_json\":\"AQID\"}]}") };
        });
        await new OpenAiImageProvider(http, new() { ApiKey = "fake" }).EditAsync(Request(true), default);
    }
    [Theory]
    [InlineData(401, false, false)]
    [InlineData(400, false, false)]
    [InlineData(429, true, false)]
    [InlineData(500, false, true)]
    public async Task AdapterClassifiesErrorsWithoutLeakingResponseBody(int status, bool retry, bool unknown)
    {
        using var http = Client(_ => Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) {
            Content = new StringContent("secret response") }));
        var error = await Assert.ThrowsAsync<ProviderException>(() => new OpenAiImageProvider(http, new()).GenerateAsync(Request(), default));
        Assert.Equal(retry, error.Retryable); Assert.Equal(unknown, error.Uncertain);
        Assert.DoesNotContain("secret", error.Message);
    }
    [Fact]
    public async Task NetworkDisconnectIsUncertainAndNeverRetriedByAdapter()
    {
        var calls = 0;
        using var http = Client(_ => { calls++; throw new HttpRequestException("sensitive-url"); });
        var error = await Assert.ThrowsAsync<ProviderException>(() => new OpenAiImageProvider(http, new()).GenerateAsync(Request(), default));
        Assert.True(error.Uncertain); Assert.Equal(1, calls);
        Assert.DoesNotContain("sensitive", error.ToString());
    }
    private static HttpClient Client(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) =>
        new(new StubHandler(send)) { BaseAddress = new Uri("https://example.invalid/v1/") };
    private sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }
}
