using CopiumCore.Api;
using CopiumCore.Application.Graph;
using CopiumCore.Infrastructure.Graph;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.AI;
using OpenAI;

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
        if (builder.Configuration["Graph:Url"] is { Length: > 0 } configuredGraphUrl)
        {
            var graphBase = new Uri(configuredGraphUrl.TrimEnd('/') + "/");
            builder.Services.AddSingleton<CopiumCore.Application.Graph.IGraphReader>(_ =>
                new DemoGraphReader(new HttpClient { BaseAddress = graphBase }));
            builder.Services.AddSingleton<IChangeWriter>(_ =>
                new HttpChangeWriter(new HttpClient { BaseAddress = graphBase }));
        }
        if (builder.Configuration.GetValue<bool>("CopiumCore:UseSqlite"))
        {
            builder.Services.AddSingleton<SqliteReviewStore>();
            builder.Services.AddSingleton<IReviewStore>(services => services.GetRequiredService<SqliteReviewStore>());
            builder.Services.AddSingleton<SqliteRunStore>();
            builder.Services.AddSingleton<IRunStore>(services => services.GetRequiredService<SqliteRunStore>());
        }
        if (demo && builder.Configuration.GetValue<bool>("CopiumCore:UseLocalModel"))
        {
            var graphUrl = builder.Configuration["Graph:Url"] ?? "http://localhost:5001/";
            var provider = builder.Configuration["CopiumCore:ModelProvider"] ?? "Ollama";
            var model = builder.Configuration["Ollama:Model"] ?? "qwen3:4b-instruct";
            if (string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
            {
                var apiKey = builder.Configuration["OpenAI:ApiKey"] ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY");
                var endpoint = builder.Configuration["OpenAI:Endpoint"];
                if (string.IsNullOrWhiteSpace(apiKey)) throw new InvalidOperationException("OpenAI provider requires OpenAI:ApiKey or OPENAI_API_KEY.");
                var clientOptions = string.IsNullOrWhiteSpace(endpoint) ? null : new OpenAIClientOptions { Endpoint = new Uri(endpoint) };
                builder.Services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(_ =>
                    new OpenAIClient(new System.ClientModel.ApiKeyCredential(apiKey), clientOptions)
                        .GetChatClient(builder.Configuration["OpenAI:Model"] ?? "gpt-4o-mini")
                        .AsIChatClient());
            }
            else
            {
                var ollamaUrl = builder.Configuration["Ollama:Url"] ?? "http://localhost:11434";
                builder.Services.AddSingleton<Microsoft.Extensions.AI.IChatClient>(_ => new OllamaSharp.OllamaApiClient(new Uri(ollamaUrl), model));
            }
            builder.Services.AddSingleton<CopiumCore.Application.Graph.IGraphReader>(_ => new CopiumCore.Infrastructure.Graph.DemoGraphReader(new HttpClient { BaseAddress = new Uri(graphUrl.TrimEnd('/') + "/") }));
            builder.Services.AddSingleton<CopiumCore.AgentRuntime.AgentExecutor>();
            builder.Services.TryAddSingleton<IAgentExecutor, LocalAgentExecutor>();
        }
        else if (demo) builder.Services.TryAddSingleton<IAgentExecutor, DemoAgentExecutor>();
        configureServices?.Invoke(builder.Services);
        var app = builder.Build();
        if (builder.Configuration.GetValue<bool>("CopiumCore:UseSqlite"))
        {
            app.Services.GetRequiredService<IReviewStore>().InitializeAsync().GetAwaiter().GetResult();
            app.Services.GetRequiredService<IRunStore>().InitializeAsync().GetAwaiter().GetResult();
        }

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
        api.MapPost("/sessions", async (SessionCreate request, DemoStore store, HttpContext context, IServiceProvider services) =>
        {
            if (string.IsNullOrWhiteSpace(request.AgentId)) return Problem(context, 422, "VALIDATION_ERROR");
            var session = store.AddSession(request.AgentId);
            if (session is null) return Problem(context, 404, "NOT_FOUND");
            var runStore = services.GetService<IRunStore>();
            if (runStore is not null) await runStore.SaveSessionAsync(session, context.RequestAborted);
            return Results.Created($"/api/llm/v1/sessions/{session.SessionId}", session);
        });
        api.MapPost("/runs", async (RunCreate request, DemoStore store, HttpContext context, IServiceProvider services, IHostApplicationLifetime lifetime) =>
        {
            var key = context.Request.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(key) || string.IsNullOrWhiteSpace(request.AgentId) || string.IsNullOrWhiteSpace(request.SessionId) ||
                string.IsNullOrWhiteSpace(request.TaskId) || string.IsNullOrWhiteSpace(request.Input)) return Problem(context, 400, "VALIDATION_ERROR");
            var executor = services.GetService<IAgentExecutor>();
            if (executor is null) return Problem(context, 503, "DEPENDENCY_UNAVAILABLE");
            var (entry, error, created) = store.Accept(key, request);
            if (error is not null) return Problem(context, error == "NOT_FOUND" ? 404 : 409, error);
            var reviewStore = services.GetService<IReviewStore>();
            var runStore = services.GetService<IRunStore>();
            if (created)
            {
                if (runStore is not null) await runStore.SaveRunAsync(entry!, context.RequestAborted);
                var runCancellation = store.RegisterCancellation(entry!.Id, lifetime.ApplicationStopping);
                _ = Task.Run(() => store.ExecuteAsync(entry!, executor, runCancellation, reviewStore, runStore));
            }
            return Results.Accepted($"/api/llm/v1/runs/{entry!.Id}", new { runId = entry.Id, status = "queued", sessionId = request.SessionId, taskId = request.TaskId });
        });
        api.MapGet("/runs/{id}", async (string id, DemoStore store, HttpContext context, IServiceProvider services) =>
        {
            var run = store.GetRun(id);
            if (run is not null) return Results.Ok(run);
            var persisted = services.GetService<IRunStore>();
            var stored = persisted is null ? null : await persisted.GetRunAsync(id, context.RequestAborted);
            return stored is null ? Problem(context, 404, "NOT_FOUND") : Results.Ok(stored);
        });
        api.MapPost("/runs/{id}/cancel", (string id, DemoStore store, HttpContext context) =>
        {
            if (!store.Cancel(id)) return Problem(context, 409, "RUN_NOT_CANCELLABLE");
            return Results.Accepted($"/api/llm/v1/runs/{id}", new { runId = id, status = "cancellationRequested" });
        });
        api.MapGet("/proposals/{id}", async (string id, DemoStore store, HttpContext context, IServiceProvider services) =>
        {
            var reviewStore = services.GetService<IReviewStore>();
            var proposal = reviewStore is null ? store.GetProposal(id) : await reviewStore.GetAsync(id, context.RequestAborted);
            return proposal is null ? Problem(context, 404, "NOT_FOUND") : Results.Ok(proposal);
        });
        api.MapGet("/proposals/{id}/audit", async (string id, HttpContext context, IServiceProvider services) =>
        {
            var reviewStore = services.GetService<IReviewStore>();
            if (reviewStore is null) return Problem(context, 503, "DEPENDENCY_UNAVAILABLE");
            return Results.Ok(new { items = await reviewStore.AuditAsync(id, context.RequestAborted) });
        });
        api.MapGet("/proposals/{id}/staleness", async (string id, HttpContext context, IServiceProvider services) =>
        {
            var reviewStore = services.GetService<IReviewStore>();
            if (reviewStore is null) return Problem(context, 503, "DEPENDENCY_UNAVAILABLE");
            var proposal = await reviewStore.GetAsync(id, context.RequestAborted);
            if (proposal is null) return Problem(context, 404, "NOT_FOUND");
            var graphReader = services.GetService<IGraphReader>();
            if (graphReader is null) return Problem(context, 503, "DEPENDENCY_UNAVAILABLE");
            var reasons = await StalenessReasonsAsync(proposal, graphReader, context.RequestAborted);
            return Results.Ok(new { stale = reasons.Count != 0, reasons });
        });
        api.MapPost("/proposals/{id}/approve", async (string id, HttpRequest request, HttpContext context, IServiceProvider services) =>
        {
            var reviewStore = services.GetService<IReviewStore>();
            if (reviewStore is null) return Problem(context, 503, "DEPENDENCY_UNAVAILABLE");
            if (!int.TryParse(request.Headers.IfMatch.ToString().Trim('"', 'W', '/'), out var version)) return Problem(context, 428, "PRECONDITION_REQUIRED");
            var key = request.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(key)) return Problem(context, 400, "VALIDATION_ERROR");
            var proposal = await reviewStore.GetAsync(id, context.RequestAborted);
            if (proposal is null) return Problem(context, 404, "NOT_FOUND");
            var graphReader = services.GetService<IGraphReader>();
            if (graphReader is not null)
            {
                var reasons = await StalenessReasonsAsync(proposal, graphReader, context.RequestAborted);
                if (reasons.Count != 0) return Problem(context, 409, "PROVENANCE_CONFLICT");
            }
            var writer = services.GetService<IChangeWriter>();
            ChangeReceipt? receipt = null;
            if (writer is not null)
            {
                var expectedTargetVersion = proposal.Evidence.FirstOrDefault(e => e.ObjectId == proposal.TargetObjectId)?.Version;
                if (expectedTargetVersion is null) return Problem(context, 409, "PROVENANCE_CONFLICT");
                var operationId = $"op-{id}-{version}";
                receipt = await writer.SetAttributeAsync(new ChangeRequest(proposal.TargetObjectId, proposal.Field, proposal.After, expectedTargetVersion, operationId), context.RequestAborted);
                if (receipt is null) return Problem(context, 409, "GRAPH_CONFLICT");
            }
            var result = await reviewStore.DecideAsync(id, "approve", null, version, key, receipt?.OperationId, context.RequestAborted);
            return result is null ? Problem(context, 409, "PROPOSAL_CONFLICT") : Results.Accepted($"/api/llm/v1/proposals/{id}", new { decision = result, change = receipt });
        });
        api.MapPost("/proposals/{id}/reject", async (string id, HttpRequest request, HttpContext context, IServiceProvider services) =>
        {
            var reviewStore = services.GetService<IReviewStore>();
            if (reviewStore is null) return Problem(context, 503, "DEPENDENCY_UNAVAILABLE");
            if (!int.TryParse(request.Headers.IfMatch.ToString().Trim('"', 'W', '/'), out var version)) return Problem(context, 428, "PRECONDITION_REQUIRED");
            var key = request.Headers["Idempotency-Key"].ToString();
            if (string.IsNullOrWhiteSpace(key)) return Problem(context, 400, "VALIDATION_ERROR");
            var result = await reviewStore.DecideAsync(id, "reject", "Rejected by reviewer", version, key, cancellationToken: context.RequestAborted);
            return result is null ? Problem(context, 409, "PROPOSAL_CONFLICT") : Results.Ok(result);
        });
        return app;
    }

    private static async Task<IReadOnlyList<StalenessReason>> StalenessReasonsAsync(ProposalView proposal, IGraphReader graphReader, CancellationToken cancellationToken)
    {
        var reasons = new List<StalenessReason>();
        foreach (var source in proposal.Evidence.DistinctBy(x => new { x.ObjectId, x.Version }))
        {
            try
            {
                var current = await graphReader.GetObjectAsync(source.ObjectId, new GraphReadContext("reviewer", $"staleness-{proposal.Id}"), cancellationToken);
                if (current is null)
                    reasons.Add(new(source.ObjectId, source.Version, null, "SOURCE_NOT_FOUND"));
                else if (!string.Equals(current.Version, source.Version, StringComparison.Ordinal))
                    reasons.Add(new(source.ObjectId, source.Version, current.Version, "SOURCE_VERSION_CHANGED"));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                reasons.Add(new(source.ObjectId, source.Version, null, "SOURCE_UNAVAILABLE"));
            }
        }
        return reasons;
    }

    private static IResult Problem(HttpContext context, int status, string code) => Results.Problem(
        statusCode: status, title: code, extensions: new Dictionary<string, object?> { ["code"] = code, ["traceId"] = context.TraceIdentifier });
}

public sealed record StalenessReason(string ObjectId, string ExpectedVersion, string? ActualVersion, string Code);
