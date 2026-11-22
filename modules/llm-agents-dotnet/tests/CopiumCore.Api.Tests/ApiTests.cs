using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CopiumCore.Api;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CopiumCore.Api.Tests;

public sealed class ApiTests
{
    [Fact]
    public async Task DemoRunIsIdempotentAndSerializesSession()
    {
        var executor = new BlockingExecutor();
        await using var app = ApiApp.Build(["--CopiumCore:Demo=true"], services => services.AddSingleton<IAgentExecutor>(executor));
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        var agentResponse = await client.PostAsJsonAsync("/api/llm/v1/agents", new { name = "agent", purpose = "test", modelProfile = "demo", systemInstruction = "test", toolAllowlist = Array.Empty<string>(), memoryPolicy = new { }, accessPolicy = new { }, enabled = true });
        Assert.Equal(HttpStatusCode.Created, agentResponse.StatusCode);
        var agent = await agentResponse.Content.ReadFromJsonAsync<JsonElement>();
        var agentId = agent.GetProperty("id").GetString()!;
        var sessionResponse = await client.PostAsJsonAsync("/api/llm/v1/sessions", new { agentId });
        Assert.Equal(HttpStatusCode.Created, sessionResponse.StatusCode);
        var session = await sessionResponse.Content.ReadFromJsonAsync<JsonElement>();
        var sessionId = session.GetProperty("sessionId").GetString()!;
        var request = new RunCreate(agentId, sessionId, "task", "input");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "key-1");
        var first = await client.PostAsJsonAsync("/api/llm/v1/runs", request);
        Assert.Equal(HttpStatusCode.Accepted, first.StatusCode);
        var accepted = await first.Content.ReadFromJsonAsync<JsonElement>();
        var id = accepted.GetProperty("runId").GetString();
        await executor.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var replay = await client.PostAsJsonAsync("/api/llm/v1/runs", request);
        Assert.Equal(id, (await replay.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("runId").GetString());
        var conflict = await client.PostAsJsonAsync("/api/llm/v1/runs", request with { Input = "other" });
        Assert.Equal("IDEMPOTENCY_CONFLICT", (await conflict.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        client.DefaultRequestHeaders.Remove("Idempotency-Key");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "key-2");
        var busy = await client.PostAsJsonAsync("/api/llm/v1/runs", request);
        Assert.Equal("SESSION_BUSY", (await busy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        executor.Release.SetResult();
        for (var i = 0; i < 100; i++)
        {
            var run = await client.GetFromJsonAsync<JsonElement>($"/api/llm/v1/runs/{id}");
            if (run.GetProperty("status").GetString() == "succeeded")
            {
                Assert.Equal("proposalCreated", run.GetProperty("result").GetProperty("outcome").GetString());
                var proposal = await client.GetFromJsonAsync<JsonElement>("/api/llm/v1/proposals/proposal-1");
                Assert.Equal("pendingReview", proposal.GetProperty("status").GetString());
                Assert.Equal("doc-9", proposal.GetProperty("evidence")[0].GetProperty("objectId").GetString());
                Assert.Equal("1", executor.Request!.Agent.Version);
                return;
            }
            await Task.Delay(20);
        }
        Assert.Fail("Run did not complete");
    }

    [Fact]
    public async Task SqliteReviewUsesEtagAndIdempotency()
    {
        var database = Path.Combine(Path.GetTempPath(), $"copiumcore-{Guid.NewGuid():N}.db");
        try
        {
            var executor = new ImmediateExecutor();
            await using var app = ApiApp.Build(["--CopiumCore:Demo=true", "--CopiumCore:UseSqlite=true", $"--CopiumCore:SqlitePath={database}"], services => services.AddSingleton<IAgentExecutor>(executor));
            app.Urls.Add("http://127.0.0.1:0");
            await app.StartAsync();
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            using var client = new HttpClient { BaseAddress = new Uri(address) };
            var agentResponse = await client.PostAsJsonAsync("/api/llm/v1/agents", new { name = "agent", purpose = "test", modelProfile = "demo", systemInstruction = "test", toolAllowlist = Array.Empty<string>(), memoryPolicy = new { }, accessPolicy = new { }, enabled = true });
            var agent = await agentResponse.Content.ReadFromJsonAsync<JsonElement>();
            var session = await (await client.PostAsJsonAsync("/api/llm/v1/sessions", new { agentId = agent.GetProperty("id").GetString() })).Content.ReadFromJsonAsync<JsonElement>();
            client.DefaultRequestHeaders.Add("Idempotency-Key", "sqlite-run");
            var accepted = await (await client.PostAsJsonAsync("/api/llm/v1/runs", new RunCreate(agent.GetProperty("id").GetString()!, session.GetProperty("sessionId").GetString()!, "task", "input"))).Content.ReadFromJsonAsync<JsonElement>();
            var runId = accepted.GetProperty("runId").GetString();
            for (var i = 0; i < 100; i++)
            {
                var run = await client.GetFromJsonAsync<JsonElement>($"/api/llm/v1/runs/{runId}");
                if (run.GetProperty("status").GetString() == "succeeded") break;
                await Task.Delay(20);
            }
            var proposalResponse = await client.GetAsync("/api/llm/v1/proposals/proposal-sqlite");
            Assert.Equal(HttpStatusCode.OK, proposalResponse.StatusCode);
            var proposal = await proposalResponse.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(1, proposal.GetProperty("version").GetInt32());
            using var approve = new HttpRequestMessage(HttpMethod.Post, "/api/llm/v1/proposals/proposal-sqlite/approve");
            approve.Headers.TryAddWithoutValidation("If-Match", "1"); approve.Headers.Add("Idempotency-Key", "approve-1");
            var decision = await client.SendAsync(approve);
            Assert.Equal(HttpStatusCode.Accepted, decision.StatusCode);
            var audit = await client.GetFromJsonAsync<JsonElement>("/api/llm/v1/proposals/proposal-sqlite/audit");
            Assert.Equal(1, audit.GetProperty("items").GetArrayLength());
            Assert.Equal("approve", audit.GetProperty("items")[0].GetProperty("action").GetString());
            Assert.Equal("approved", audit.GetProperty("items")[0].GetProperty("status").GetString());
            using var replay = new HttpRequestMessage(HttpMethod.Post, "/api/llm/v1/proposals/proposal-sqlite/approve");
            replay.Headers.TryAddWithoutValidation("If-Match", "1"); replay.Headers.Add("Idempotency-Key", "approve-1");
            Assert.Equal(HttpStatusCode.Accepted, (await client.SendAsync(replay)).StatusCode);
            using var conflict = new HttpRequestMessage(HttpMethod.Post, "/api/llm/v1/proposals/proposal-sqlite/approve");
            conflict.Headers.TryAddWithoutValidation("If-Match", "1"); conflict.Headers.Add("Idempotency-Key", "approve-2");
            Assert.Equal(HttpStatusCode.Conflict, (await client.SendAsync(conflict)).StatusCode);
        }
        finally
        {
            if (File.Exists(database)) File.Delete(database);
        }
    }

    [Fact]
    public async Task DemoMustBeExplicitlyEnabled()
    {
        await using var app = ApiApp.Build([]);
        app.Urls.Add("http://127.0.0.1:0");
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        using var client = new HttpClient { BaseAddress = new Uri(address) };
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/api/llm/v1/agents")).StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await client.GetAsync("/ready")).StatusCode);
    }

    private sealed class ImmediateExecutor : IAgentExecutor
    {
        public Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
        {
            var source = new SourceRef("doc-9", "3", "groupCode");
            return Task.FromResult(new ExecutionResult("proposalCreated", "test", [source],
                [new PreparedProposal("proposal-sqlite", "req-17", "groupCode", JsonSerializer.SerializeToElement<object?>(null), JsonSerializer.SerializeToElement("ИУ5-31Б"), "decision", [source])]));
        }
    }

    private sealed class BlockingExecutor : IAgentExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ExecutionRequest? Request { get; private set; }
        public async Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            Started.SetResult();
            await Release.Task.WaitAsync(cancellationToken);
            var source = new SourceRef("doc-9", "3", "groupCode");
            return new ExecutionResult("proposalCreated", "test", [source],
                [new PreparedProposal("proposal-1", "req-17", "groupCode", JsonSerializer.SerializeToElement<object?>(null), JsonSerializer.SerializeToElement("ИУ5-31Б"), "decision", [source])]);
        }
    }
}
