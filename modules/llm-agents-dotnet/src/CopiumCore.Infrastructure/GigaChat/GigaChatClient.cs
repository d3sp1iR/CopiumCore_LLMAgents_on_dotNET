using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.AI;

namespace CopiumCore.Infrastructure.GigaChat;

public sealed class GigaChatClient : IChatClient
{
    private readonly HttpClient _http;
    private readonly string _credentials;
    private readonly string _model;
    private readonly Uri _apiBase;
    private readonly Uri _oauthEndpoint;
    private string? _accessToken;

    public GigaChatClient(HttpClient http, string credentials, string model, string apiBase, string oauthEndpoint)
    {
        _http = http;
        _credentials = credentials;
        _model = model;
        _apiBase = new Uri(apiBase.TrimEnd('/') + "/");
        _oauthEndpoint = new Uri(oauthEndpoint);
    }

    public async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(messages);
        var token = await GetTokenAsync(cancellationToken);
        var body = new JsonObject
        {
            ["model"] = _model,
            ["messages"] = new JsonArray(messages.Select(ToJson).ToArray()),
            ["stream"] = false
        };
        if (options?.Temperature is { } temperature) body["temperature"] = temperature;
        if (options?.MaxOutputTokens is { } maxTokens) body["max_tokens"] = maxTokens;
        if (options?.Tools is { Count: > 0 } tools)
            body["tools"] = new JsonArray(tools.OfType<AIFunction>().Select(ToToolJson).ToArray());

        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(_apiBase, "chat/completions"));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        using var response = await _http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GigaChat chat request failed ({(int)response.StatusCode}): {payload}");

        using var document = JsonDocument.Parse(payload);
        var message = document.RootElement.GetProperty("choices")[0].GetProperty("message");
        var contents = new List<AIContent>();
        if (message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String)
            contents.Add(new TextContent(content.GetString() ?? string.Empty));
        if (message.TryGetProperty("tool_calls", out var calls) && calls.ValueKind == JsonValueKind.Array)
        {
            foreach (var call in calls.EnumerateArray())
            {
                var function = call.GetProperty("function");
                var arguments = JsonSerializer.Deserialize<Dictionary<string, object?>>(function.GetProperty("arguments").GetString() ?? "{}") ?? [];
                contents.Add(new FunctionCallContent(call.GetProperty("id").GetString() ?? Guid.NewGuid().ToString("N"),
                    function.GetProperty("name").GetString() ?? string.Empty, arguments));
            }
        }
        var responseMessage = new ChatMessage(ChatRole.Assistant, contents) { RawRepresentation = message.Clone() };
        return new ChatResponse(responseMessage) { ModelId = _model };
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages,
        ChatOptions? options = null, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        yield return new ChatResponseUpdate(ChatRole.Assistant, (await GetResponseAsync(messages, options, cancellationToken)).Text);
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => serviceType == typeof(IChatClient) ? this : null;

    private async Task<string> GetTokenAsync(CancellationToken cancellationToken)
    {
        if (_accessToken is not null) return _accessToken;
        using var request = new HttpRequestMessage(HttpMethod.Post, _oauthEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", _credentials);
        request.Headers.Add("RqUID", Guid.NewGuid().ToString());
        request.Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["scope"] = "GIGACHAT_API_PERS" });
        using var response = await _http.SendAsync(request, cancellationToken);
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GigaChat token request failed ({(int)response.StatusCode}): {payload}");
        using var document = JsonDocument.Parse(payload);
        return _accessToken = document.RootElement.GetProperty("access_token").GetString()
            ?? throw new InvalidDataException("GigaChat token response did not contain access_token.");
    }

    private static JsonObject ToJson(ChatMessage message)
    {
        var role = message.Role == ChatRole.System ? "system" : message.Role == ChatRole.User ? "user" : "assistant";
        var result = new JsonObject { ["role"] = role };
        var text = message.Text;
        if (!string.IsNullOrEmpty(text)) result["content"] = text;
        var calls = message.Contents.OfType<FunctionCallContent>().ToArray();
        if (calls.Length > 0)
        {
            result["tool_calls"] = new JsonArray(calls.Select(call => new JsonObject
            {
                ["id"] = call.CallId,
                ["type"] = "function",
                ["function"] = new JsonObject { ["name"] = call.Name, ["arguments"] = JsonSerializer.Serialize(call.Arguments) }
            }).ToArray());
        }
        var results = message.Contents.OfType<FunctionResultContent>().ToArray();
        if (results.Length > 0)
        {
            result["role"] = "tool";
            result["tool_call_id"] = results[0].CallId;
            result["content"] = JsonSerializer.Serialize(results[0].Result);
        }
        return result;
    }

    private static JsonObject ToToolJson(AIFunctionDeclaration function) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = function.Name,
            ["description"] = function.Description,
            ["parameters"] = JsonNode.Parse(function.JsonSchema.GetRawText())
        }
    };

    public void Dispose() => _http.Dispose();
}
