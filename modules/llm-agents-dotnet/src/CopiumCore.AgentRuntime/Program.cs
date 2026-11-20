using System.Text.Json;
using CopiumCore.AgentRuntime;
using CopiumCore.Infrastructure.Graph;
using Microsoft.Extensions.AI;
using OllamaSharp;

if (args.Length == 0 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Usage: dotnet run --project src/CopiumCore.AgentRuntime -- \"question\"");
    return 2;
}

var ollamaUrl = Environment.GetEnvironmentVariable("OLLAMA_URL") ?? "http://localhost:11434";
var model = Environment.GetEnvironmentVariable("OLLAMA_MODEL") ?? "qwen3:4b-instruct";
var graphUrl = Environment.GetEnvironmentVariable("GRAPH_URL") ?? "http://localhost:5000/";
if (!graphUrl.EndsWith('/')) graphUrl += "/";
using var graphClient = new HttpClient { BaseAddress = new Uri(graphUrl) };
using IChatClient chatClient = new OllamaApiClient(new Uri(ollamaUrl), model);
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };

try
{
    IAgentExecutor executor = new AgentExecutor(chatClient, new DemoGraphReader(graphClient));
    var request = new ExecutionRequest(args[0], "local-user", Guid.NewGuid().ToString("N"));
    var result = await executor.ExecuteAsync(request, cancellation.Token);
    Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
    return 0;
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("Cancelled.");
    return 130;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"Execution failed: {ex.Message}");
    return 1;
}
