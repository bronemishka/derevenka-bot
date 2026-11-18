using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DerevenkaBot.Core.Application;
using DerevenkaBot.Core.Domain;

namespace DerevenkaBot.Core.Infrastructure;

public sealed class OpenAiChatProvider(HttpClient http, ImageOptions images, ChatOptions options) : IChatProvider
{
    public Task<string> ReplyAsync(IReadOnlyList<ChatMessage> messages, CancellationToken ct) =>
        ReplyStreamingAsync(messages, _ => { }, ct);

    public async Task<string> ReplyStreamingAsync(IReadOnlyList<ChatMessage> messages, Action<string> onText, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "responses");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", images.ApiKey);
        request.Content = JsonContent.Create(new
        {
            model = options.Model,
            instructions = "Ты — дружелюбный помощник Telegram-бота #деревенька. Отвечай на языке собеседника, " +
                "по умолчанию по-русски. Помогай с вопросами, объяснениями и идеями. Пиши понятно, без HTML-разметки. " +
                "Не утверждай, что проверил интернет: доступ к поиску не подключён. Если не знаешь, скажи об этом. " +
                "Для создания картинки предложи /imagine с описанием, для изменения — /edit с инструкцией.",
            input = messages.Select(x => new { role = x.Role, content = x.Content }),
            max_output_tokens = options.MaxOutputTokens,
            stream = true,
            store = false
        });
        // Never repeat a paid POST automatically, even when the stream ends unexpectedly.
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new ProviderException($"chat_http_{(int)response.StatusCode}");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        try
        {
            // Also accept complete JSON from compatible non-streaming transports.
            if (response.Content.Headers.ContentType?.MediaType == "application/json")
            {
                using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                var text = ReadAnswer(json.RootElement);
                onText(text);
                return text;
            }
            using var reader = new StreamReader(stream);
            var textBuffer = new StringBuilder();
            var eventData = new StringBuilder();
            var completed = false;
            string? finalAnswer = null;
            void HandleEvent()
            {
                var data = eventData.ToString(); eventData.Clear();
                if (data.Length == 0 || data == "[DONE]") return;
                using var json = JsonDocument.Parse(data);
                var root = json.RootElement;
                var type = root.GetProperty("type").GetString();
                if (type is "response.output_text.delta" or "response.refusal.delta")
                {
                    textBuffer.Append(root.GetProperty("delta").GetString());
                    onText(textBuffer.ToString());
                }
                if (type is "error" or "response.failed" or "response.incomplete")
                    throw new ProviderException("chat_stream_failed");
                if (type == "response.completed")
                {
                    completed = true;
                    if (root.TryGetProperty("response", out var result)) finalAnswer = ReadAnswer(result);
                }
            }
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0)
                {
                    HandleEvent();
                    if (completed) break;
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (eventData.Length > 0) eventData.Append('\n');
                    eventData.Append(line.AsSpan(5).TrimStart());
                }
            }
            if (!completed && eventData.Length > 0) HandleEvent();
            if (!completed) throw new ProviderException("chat_stream_interrupted", uncertain: true);
            var answer = finalAnswer ?? textBuffer.ToString();
            if (string.IsNullOrWhiteSpace(answer)) throw new ProviderException("chat_empty_response");
            onText(answer);
            return answer;
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new ProviderException("chat_invalid_response"); }
    }

    private static string ReadAnswer(JsonElement root)
    {
        if (root.TryGetProperty("error", out var error) && error.ValueKind != JsonValueKind.Null)
            throw new ProviderException("chat_response_error");
        if (root.TryGetProperty("status", out var status) && status.GetString() is "failed" or "incomplete")
            throw new ProviderException("chat_response_incomplete");
        var parts = new List<string>();
        if (root.TryGetProperty("output", out var output))
            foreach (var item in output.EnumerateArray())
                if (item.TryGetProperty("type", out var type) && type.GetString() == "message" &&
                    item.TryGetProperty("content", out var content))
                    foreach (var part in content.EnumerateArray())
                    {
                        var kind = part.GetProperty("type").GetString();
                        if (kind == "output_text") parts.Add(part.GetProperty("text").GetString()!);
                        if (kind == "refusal") parts.Add(part.GetProperty("refusal").GetString()!);
                    }
        var answer = string.Join("\n\n", parts);
        if (string.IsNullOrWhiteSpace(answer)) throw new ProviderException("chat_empty_response");
        return answer;
    }
}
