using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Domain;

namespace DerevenkaBot.Core.Infrastructure;

// Deliberately no automatic HttpClient retries on this paid POST endpoint.
public sealed class OpenAiImageProvider(HttpClient http, ImageOptions options) : IImageGenerationProvider
{
    public Task<ImageProviderResult> GenerateAsync(ImageProviderRequest request, CancellationToken ct) => Call(request, false, ct);
    public Task<ImageProviderResult> EditAsync(ImageProviderRequest request, CancellationToken ct) => Call(request, true, ct);
    private async Task<ImageProviderResult> Call(ImageProviderRequest r, bool edit, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, edit ? "images/edits" : "images/generations");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
        request.Headers.Add("X-Client-Request-Id", r.CorrelationId.ToString());
        var fields = new Dictionary<string, object>
        { ["model"] = r.Model, ["prompt"] = r.Prompt, ["quality"] = r.Quality, ["size"] = r.Size,
            ["n"] = 1, ["output_format"] = "png", ["safety_identifier"] = r.SafetyIdentifier };
        if (r.Complexity != null) fields["complexity"] = r.Complexity;
        if (edit)
        {
            var form = new MultipartFormDataContent();
            foreach (var (key, value) in fields) form.Add(new StringContent(value.ToString()!), key);
            foreach (var asset in r.InputAssets)
            {
                var content = new ByteArrayContent(asset.Bytes);
                content.Headers.ContentType = new MediaTypeHeaderValue(asset.ContentType);
                form.Add(content, "image[]", "reference.png");
            }
            request.Content = form;
        }
        else request.Content = JsonContent.Create(fields);
        HttpResponseMessage response;
        try { response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct); }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException)
        { throw new ProviderException("network_unknown", uncertain: true); }
        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                var status = (int)response.StatusCode;
                // A 5xx can happen after work has begun. Conservatively retain its reservation.
                throw new ProviderException($"http_{status}", retryable: status == 429, uncertain: status >= 500);
            }
            try
            {
                await using var stream = await response.Content.ReadAsStreamAsync(ct);
                using var limited = new MemoryStream();
                var buffer = new byte[81920];
                int count;
                while ((count = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    if (limited.Length + count > options.MaxInputFileMb * 1024L * 1024 * 2)
                        throw new ProviderException("response_size", uncertain: true);
                    await limited.WriteAsync(buffer.AsMemory(0, count), ct);
                }
                limited.Position = 0;
                using var json = await JsonDocument.ParseAsync(limited, cancellationToken: ct);
                var root = json.RootElement;
                var bytes = Convert.FromBase64String(root.GetProperty("data")[0].GetProperty("b64_json").GetString()!);
                string? usage = root.TryGetProperty("usage", out var u) ? u.GetRawText() : null;
                decimal? cost = null;
                if (usage != null && options.InputTextUsdPerMillion is { } textRate && options.InputImageUsdPerMillion is { } imageRate &&
                    options.OutputImageUsdPerMillion is { } outputRate && u.TryGetProperty("input_tokens_details", out var detail) &&
                    detail.TryGetProperty("text_tokens", out var textTokens) && detail.TryGetProperty("image_tokens", out var imageTokens) &&
                    u.TryGetProperty("output_tokens", out var outputTokens))
                    cost = (textTokens.GetInt64() * textRate + imageTokens.GetInt64() * imageRate + outputTokens.GetInt64() * outputRate) / 1_000_000m;
                return new(bytes, "image/png", r.Model,
                    response.Headers.TryGetValues("x-request-id", out var ids) ? ids.FirstOrDefault() : null, usage, cost);
            }
            catch (ProviderException) { throw; }
            catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or IOException or OperationCanceledException)
            { throw new ProviderException("response_unknown", uncertain: true); }
        }
    }
}
