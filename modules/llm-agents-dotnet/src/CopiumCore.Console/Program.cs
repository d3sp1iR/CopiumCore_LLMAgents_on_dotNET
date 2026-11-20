using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

return await ConsoleApp.RunAsync(args);

internal static class ConsoleApp
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            var options = ParseArguments(args);
            if (options.ContainsKey("help"))
            {
                System.Console.WriteLine("Usage: CopiumCore.Console --settings FILE [--base-url URL] [--input TEXT] [--task-id ID] [--idempotency-key KEY] [--poll-interval-seconds N] [--timeout-seconds N]\nSettings JSON: baseUrl, input, taskId, idempotencyKey (optional), bearerToken (optional), pollIntervalSeconds, timeoutSeconds, agent { name, purpose, modelProfile, systemInstruction, toolAllowlist, memoryPolicy, accessPolicy, enabled }. Command-line values override settings.");
                return 0;
            }

            if (!options.TryGetValue("settings", out var settingsPath))
                throw new ArgumentException("Укажите файл настроек через --settings. Справка: --help.");
            var settings = JsonSerializer.Deserialize<Settings>(await File.ReadAllTextAsync(settingsPath), JsonOptions)
                ?? throw new ArgumentException("Файл настроек пуст.");
            settings.BaseUrl = GetOption(options, "base-url", settings.BaseUrl);
            settings.Input = GetOption(options, "input", settings.Input);
            settings.TaskId = GetOption(options, "task-id", settings.TaskId);
            settings.IdempotencyKey = GetOption(options, "idempotency-key", settings.IdempotencyKey);
            settings.PollIntervalSeconds = GetNumber(options, "poll-interval-seconds", settings.PollIntervalSeconds);
            settings.TimeoutSeconds = GetNumber(options, "timeout-seconds", settings.TimeoutSeconds);
            Validate(settings);

            using var client = new HttpClient { BaseAddress = new Uri(settings.BaseUrl!.TrimEnd('/') + "/") };
            if (!string.IsNullOrWhiteSpace(settings.BearerToken))
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.BearerToken);
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            System.Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
            var token = cancellation.Token;

            var agent = await SendAsync(client, HttpMethod.Post, "api/llm/v1/agents", settings.Agent, HttpStatusCode.Created, null, token);
            var agentId = RequiredString(agent, "id");
            System.Console.WriteLine($"Агент: {agentId}");
            var session = await SendAsync(client, HttpMethod.Post, "api/llm/v1/sessions", new { agentId }, HttpStatusCode.Created, null, token);
            var sessionId = RequiredString(session, "sessionId");
            System.Console.WriteLine($"Сессия: {sessionId}");
            var key = settings.IdempotencyKey ?? Guid.NewGuid().ToString();
            var accepted = await SendAsync(client, HttpMethod.Post, "api/llm/v1/runs", new { agentId, sessionId, taskId = settings.TaskId, input = settings.Input }, HttpStatusCode.Accepted, key, token);
            var runId = RequiredString(accepted, "runId");
            System.Console.WriteLine($"Запуск: {runId}; Idempotency-Key: {key}");
            while (true)
            {
                var run = await SendAsync(client, HttpMethod.Get, $"api/llm/v1/runs/{Uri.EscapeDataString(runId)}", null, HttpStatusCode.OK, null, token);
                var status = RequiredString(run, "status");
                System.Console.WriteLine($"Статус: {status}");
                switch (status)
                {
                    case "queued":
                    case "running":
                        await Task.Delay(TimeSpan.FromSeconds(settings.PollIntervalSeconds), token);
                        break;
                    case "succeeded":
                        await ShowResultAsync(client, run, token);
                        return 0;
                    case "failed":
                    case "cancelled":
                    case "timedOut":
                        throw new InvalidOperationException($"Запуск завершился со статусом {status}; errorCode: {OptionalString(run, "errorCode") ?? "не указан"}.");
                    default:
                        throw new InvalidOperationException($"Неизвестный статус запуска: {status}.");
                }
            }
        }
        catch (OperationCanceledException)
        {
            System.Console.Error.WriteLine("Ошибка: операция отменена или превышено время ожидания.");
            return 1;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or JsonException or HttpRequestException or InvalidOperationException or UnauthorizedAccessException)
        {
            System.Console.Error.WriteLine($"Ошибка: {ex.Message}");
            return 1;
        }
    }

    private static async Task<JsonElement> SendAsync(HttpClient client, HttpMethod method, string path, object? body, HttpStatusCode expected, string? key, CancellationToken token)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        using var response = await client.SendAsync(request, token);
        var content = await response.Content.ReadAsStringAsync(token);
        if (response.StatusCode != expected)
        {
            var retry = response.Headers.RetryAfter is null ? "" : $"; Retry-After: {response.Headers.RetryAfter}";
            throw new InvalidOperationException($"{method} {path}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}{retry}; ответ: {content}");
        }
        using var document = JsonDocument.Parse(content);
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidOperationException($"{method} {path}: ожидался JSON-объект.");
        return document.RootElement.Clone();
    }

    private static async Task ShowResultAsync(HttpClient client, JsonElement run, CancellationToken token)
    {
        if (!run.TryGetProperty("result", out var result) || result.ValueKind != JsonValueKind.Object)
            throw new InvalidOperationException("Успешный запуск не содержит result.");
        System.Console.WriteLine($"Результат: {RequiredString(result, "outcome")}");
        if (!result.TryGetProperty("summary", out var summary) || summary.ValueKind != JsonValueKind.String)
            throw new InvalidOperationException("В результате отсутствует summary.");
        System.Console.WriteLine($"Описание: {summary.GetString()}");
        var evidence = RequiredArray(result, "evidence");
        System.Console.WriteLine("Источники:");
        foreach (var source in evidence.EnumerateArray())
            System.Console.WriteLine($"  {RequiredString(source, "objectId")} (версия {RequiredString(source, "version")}, поле: {OptionalString(source, "field") ?? "не указано"})");
        var proposals = RequiredArray(result, "proposalIds");
        System.Console.WriteLine("Предложения:");
        foreach (var proposal in proposals.EnumerateArray())
        {
            if (proposal.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(proposal.GetString()))
                throw new InvalidOperationException("Некорректный идентификатор предложения.");
            var id = proposal.GetString()!;
            var card = await SendAsync(client, HttpMethod.Get, $"api/llm/v1/proposals/{Uri.EscapeDataString(id)}", null, HttpStatusCode.OK, null, token);
            System.Console.WriteLine($"  {id}: {RequiredString(card, "status")}");
            System.Console.WriteLine($"    {RequiredString(card, "targetObjectId")}.{RequiredString(card, "field")}: {card.GetProperty("before")} → {card.GetProperty("after")}");
            System.Console.WriteLine($"    Основание: {RequiredString(card, "reason")}");
        }
        if (proposals.GetArrayLength() == 0) System.Console.WriteLine("  нет");
        if (evidence.GetArrayLength() == 0) System.Console.WriteLine("Источники отсутствуют.");
    }

    private static JsonElement RequiredArray(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value : throw new InvalidOperationException($"В ответе отсутствует массив {name}.");
    private static string RequiredString(JsonElement obj, string name) => OptionalString(obj, name) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
        ? value : throw new InvalidOperationException($"В ответе отсутствует строка {name}.");
    private static string? OptionalString(JsonElement obj, string name) =>
        obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Dictionary<string, string> ParseArguments(string[] args)
    {
        var allowed = new HashSet<string> { "settings", "base-url", "input", "task-id", "idempotency-key", "poll-interval-seconds", "timeout-seconds", "help" };
        var values = new Dictionary<string, string>();
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal) || !allowed.Contains(args[i][2..])) throw new ArgumentException($"Неизвестный аргумент: {args[i]}.");
            var name = args[i][2..];
            if (values.ContainsKey(name)) throw new ArgumentException($"Аргумент --{name} указан повторно.");
            if (name == "help") { values.Add(name, "true"); continue; }
            if (++i == args.Length || args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"Для --{name} требуется значение.");
            values.Add(name, args[i]);
        }
        return values;
    }
    private static string? GetOption(Dictionary<string, string> options, string name, string? fallback) => options.TryGetValue(name, out var value) ? value : fallback;
    private static int GetNumber(Dictionary<string, string> options, string name, int fallback) =>
        !options.TryGetValue(name, out var value) ? fallback : int.TryParse(value, out var number) ? number : throw new ArgumentException($"--{name}: требуется целое число.");
    private static void Validate(Settings settings)
    {
        if (!Uri.TryCreate(settings.BaseUrl, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException("baseUrl должен быть абсолютным HTTP(S) URL без query и fragment.");
        if (string.IsNullOrWhiteSpace(settings.Input) || string.IsNullOrWhiteSpace(settings.TaskId)) throw new ArgumentException("Укажите input и taskId.");
        if (settings.IdempotencyKey is not null && string.IsNullOrWhiteSpace(settings.IdempotencyKey)) throw new ArgumentException("idempotencyKey не может быть пустым.");
        if (settings.PollIntervalSeconds <= 0 || settings.TimeoutSeconds <= 0) throw new ArgumentException("Интервал опроса и таймаут должны быть положительными.");
        var agent = settings.Agent;
        if (agent is null || string.IsNullOrWhiteSpace(agent.Name) || string.IsNullOrWhiteSpace(agent.Purpose) || string.IsNullOrWhiteSpace(agent.ModelProfile) || string.IsNullOrWhiteSpace(agent.SystemInstruction) || agent.ToolAllowlist is null || agent.ToolAllowlist.Any(string.IsNullOrWhiteSpace) || agent.MemoryPolicy.ValueKind != JsonValueKind.Object || agent.AccessPolicy.ValueKind != JsonValueKind.Object || agent.Enabled is null)
            throw new ArgumentException("Укажите все обязательные поля agent: name, purpose, modelProfile, systemInstruction, toolAllowlist, memoryPolicy, accessPolicy, enabled.");
    }
}

internal sealed class Settings
{
    public string? BaseUrl { get; set; }
    public string? Input { get; set; }
    public string? TaskId { get; set; }
    public string? IdempotencyKey { get; set; }
    public string? BearerToken { get; set; }
    public int PollIntervalSeconds { get; set; } = 1;
    public int TimeoutSeconds { get; set; } = 300;
    public AgentSettings? Agent { get; set; }
}

internal sealed class AgentSettings
{
    public string? Name { get; set; }
    public string? Purpose { get; set; }
    public string? ModelProfile { get; set; }
    public string? SystemInstruction { get; set; }
    public string[]? ToolAllowlist { get; set; }
    public JsonElement MemoryPolicy { get; set; }
    public JsonElement AccessPolicy { get; set; }
    public bool? Enabled { get; set; }
}
