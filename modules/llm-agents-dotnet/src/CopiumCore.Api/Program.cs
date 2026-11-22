using CopiumCore.Api;
using Microsoft.Extensions.DependencyInjection.Extensions;

var app = ApiApp.Build(args);
app.Run();

public static class ApiApp
{
    public static WebApplication Build(string[] args, Action<IServiceCollection>? configureServices = null)
    {
        var builder = WebApplication.CreateBuilder(args);
        var demo = builder.Configuration.GetValue<bool>("CopiumCore:Demo");
        if (builder.Configuration.GetValue<bool>("CopiumCore:UseSqlite"))
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_sqlite3());
        builder.Services.AddSingleton<DemoStore>();
        if (builder.Configuration.GetValue<bool>("CopiumCore:UseSqlite"))
        {
            builder.Services.AddSingleton<SqliteReviewStore>();
            builder.Services.AddSingleton<IReviewStore>(services => services.GetRequiredService<SqliteReviewStore>());
        }
        if (demo && builder.Configuration.GetValue<bool>("CopiumCore:UseLocalModel"))
        {
            var graphUrl = builder.Configuration["Graph:Url"] ?? "http://localhost:5001/";
            var ollamaUrl = builder.Configuration["Ollama:Url"] ?? "http://localhost:11434";
            var model = builder.Configuration["Ollama:Model"] ?? "qwen3:4b-instruct";
            builder.Services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(_ => new OllamaSharp.OllamaApiClient(new Uri(ollamaUrl), model));
            builder.Services.AddSingleton<CopiumCore.Application.Graph.IGraphReader>(_ => new CopiumCore.Infrastructure.Graph.DemoGraphReader(new HttpClient { BaseAddress = new Uri(graphUrl.TrimEnd('/') + "/") }));
            builder.Services.AddSingleton<CopiumCore.AgentRuntime.AgentExecutor>();
            builder.Services.TryAddSingleton<IAgentExecutor, LocalAgentExecutor>();
        }
        else if (demo) builder.Services.TryAddSingleton<IAgentExecutor, DemoAgentExecutor>();
        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        if (builder.Configuration.GetValue<bool>("CopiumCore:UseSqlite"))
            app.Services.GetRequiredService<IReviewStore>().InitializeAsync().GetAwaiter().GetResult();

        app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
        app.MapGet("/ready", () => Results.Json(new { status = "notReady", reason = "Production runtime and dependencies are not implemented" }, statusCode: 503));
        var api = app.MapGroup("/api/llm/v1");
        api.AddEndpointFilter(async (context, next) => demo
            ? await next(context)
            : Problem(context.HttpContext, 503, "DEPENDENCY_UNAVAILABLE"));

        api.MapPost("/agents", (AgentCreate request, DemoStore store, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.Name) || string.IsNullOrWhiteSpace(request.Purpose) ||
                string.IsNullOrWhiteSpace(request.ModelProfile) || string.IsNullOrWhiteSpace(request.SystemInstruction) ||
                request.ToolAllowlist is null || request.ToolAllowlist.Any(string.IsNullOrWhiteSpace) ||
                request.MemoryPolicy.ValueKind != System.Text.Json.JsonValueKind.Object || request.AccessPolicy.ValueKind != System.Text.Json.JsonValueKind.Object)
                return Problem(context, 422, "VALIDATION_ERROR");
            var agent = store.AddAgent(request);
            return Results.Created($"/api/llm/v1/agents/{agent.Id}", agent);
        });
        api.MapGet("/agents", (DemoStore store) => Results.Ok(new { items = store.GetAgents(), nextCursor = (string?)null }));
        api.MapGet("/agents/{id}", (string id, DemoStore store, HttpContext context) =>
        {
            var agent = store.GetAgent(id);
            return agent is null ? Problem(context, 404, "NOT_FOUND") : Results.Ok(agent);
        });
        api.MapPost("/sessions", (SessionCreate request, DemoStore store, HttpContext context) =>
        {
            if (string.IsNullOrWhiteSpace(request.AgentId)) return Problem(context, 422, "VALIDATION_ERROR");
            var session = store.AddSession(request.AgentId);
            return session is null ? Problem(context, 404, "NOT_FOUND") : Results.Created($"/api/llm/v1/sessions/{session.SessionId}", session);
        });
        api.MapPost("/runs", (RunCreate request, DemoStore store, HttpContext context, IServiceProvider services, IHostApplicationLifetime lifetime) =>
        {
            var key = context.Request.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(request.AgentId) || string.IsNullOrWhiteSpace(request.SessionId) ||
                string.IsNullOrWhiteSpace(request.TaskId) || string.IsNullOrWhiteSpace(request.Input)) return Problem(context, 400, "VALIDATION_ERROR");
            var executor = services.GetService<IAgentExecutor>();
            if (executor is null) return Problem(context, 503, "DEPENDENCY_UNAVAILABLE");
            var (entry, error, created) = store.Accept(key, request);
            if (error is not null) return Problem(context, error == "NOT_FOUND" ? 404 : 409, error);
            var reviewStore = services.GetService<IReviewStore>();
            if (created) _ = Task.Run(() => store.ExecuteAsync(entry!, executor, lifetime.ApplicationStopping, reviewStore));
            return Results.Accepted($"/api/llm/v1/runs/{entry!.Id}", new { runId = entry.Id, status = "queued", sessionId = request.SessionId, taskId = request.TaskId });
        });
        api.MapGet("/runs/{id}", (string id, DemoStore store, HttpContext context) =>
        {
            var run = store.GetRun(id);
            return run is null ? Problem(context, 404, "NOT_FOUND") : Results.Ok(run);
        });
        api.MapGet("/proposals/{id}", async (string id, DemoStore store, HttpContext context, IServiceProvider services) =>
        {
            var reviewStore = services.GetService<IReviewStore>();
            var proposal = reviewStore is null ? store.GetProposal(id) : await reviewStore.GetAsync(id, context.RequestAborted);
            return proposal is null ? Problem(context, 404, "NOT_FOUND") : Results.Ok(proposal);
        });
        api.MapPost("/proposals/{id}/approve", async (string id, HttpRequest request, HttpContext context, IServiceProvider services) =>
        {
            var reviewStore = services.GetService<IReviewStore>();
            if (reviewStore is null) return Problem(context, 503, "DEPENDENCY_UNAVAILABLE");
            if (!int.TryParse(request.Headers.IfMatch.ToString().Trim('"', 'W', '/'), out var version)) return Problem(context, 428, "PRECONDITION_REQUIRED");
            var key = request.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(key)) return Problem(context, 400, "VALIDATION_ERROR");
            var result = await reviewStore.DecideAsync(id, "approve", null, version, key, context.RequestAborted);
            return result is null ? Problem(context, 409, "PROPOSAL_CONFLICT") : Results.Accepted($"/api/llm/v1/proposals/{id}", result);
        });
        api.MapPost("/proposals/{id}/reject", async (string id, HttpRequest request, HttpContext context, IServiceProvider services) =>
        {
            var reviewStore = services.GetService<IReviewStore>();
            if (reviewStore is null) return Problem(context, 503, "DEPENDENCY_UNAVAILABLE");
            if (!int.TryParse(request.Headers.IfMatch.ToString().Trim('"', 'W', '/'), out var version)) return Problem(context, 428, "PRECONDITION_REQUIRED");
            var key = request.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(key)) return Problem(context, 400, "VALIDATION_ERROR");
            var result = await reviewStore.DecideAsync(id, "reject", "Rejected by reviewer", version, key, context.RequestAborted);
            return result is null ? Problem(context, 409, "PROPOSAL_CONFLICT") : Results.Ok(result);
        });
        return app;
    }

    private static IResult Problem(HttpContext context, int status, string code) => Results.Problem(
        statusCode: status, title: code, extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = context.TraceIdentifier });
}
