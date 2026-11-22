using System.Collections.Concurrent;
using System.Text.Json;

var app = TestGraphApp.Build(args);
app.Run();

public sealed class GraphFixtureObject(string id, string type, int version, Dictionary<string, JsonElement> attributes)
{
    public string Id { get; } = id;
    public string Type { get; } = type;
    public int Version { get; set; } = version;
    public Dictionary<string, JsonElement> Attributes { get; } = attributes;
}

public sealed record GraphChange(string OperationId, string ObjectId, string Field, string Version, bool Applied);

public static class TestGraphApp
{
    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton<GraphFixture>();
        var app = builder.Build();

        app.Logger.LogWarning("Test Graph API is an insecure demonstration service; do not expose real data.");
        app.MapGet("/objects/{id}", (string id, GraphFixture fixture) =>
        {
            var value = fixture.Get(id);
            return value is null ? Results.NotFound() : Results.Ok(value);
        });
        app.MapPut("/objects/{id}/attributes/{field}", (string id, string field, GraphWriteRequest request, GraphFixture fixture) =>
        {
            var result = fixture.SetAttribute(id, field, request);
            return result switch
            {
                null => Results.NotFound(),
                { Applied: false } => Results.Conflict(new { code = "GRAPH_CONFLICT" }),
                _ => Results.Ok(result)
            };
        });
        return app;
    }
}

public sealed record GraphWriteRequest(JsonElement Value, string ExpectedVersion, string OperationId);

public sealed class GraphFixture
{
    private readonly object gate = new();
    private readonly Dictionary<string, GraphFixtureObject> objects = new(StringComparer.Ordinal)
    {
        ["req-17"] = new("req-17", "Application", 7, new() {
            ["groupCode"] = JsonSerializer.SerializeToElement<string?>(null),
            ["projectId"] = JsonSerializer.SerializeToElement("project-4"),
            ["decisionId"] = JsonSerializer.SerializeToElement("doc-9")
        }),
        ["doc-9"] = new("doc-9", "Decision", 3, new() {
            ["applicationId"] = JsonSerializer.SerializeToElement("req-17"),
            ["groupCode"] = JsonSerializer.SerializeToElement("ИУ5-31Б"),
            ["approved"] = JsonSerializer.SerializeToElement(true)
        })
    };
    private readonly Dictionary<string, GraphChange> operations = new(StringComparer.Ordinal);

    public object? Get(string id)
    {
        lock (gate)
        {
            if (!objects.TryGetValue(id, out var value)) return null;
            return new { value.Id, value.Type, version = value.Version.ToString(), attributes = value.Attributes };
        }
    }

    public GraphChange? SetAttribute(string id, string field, GraphWriteRequest request)
    {
        lock (gate)
        {
            if (operations.TryGetValue(request.OperationId, out var prior)) return prior;
            if (!objects.TryGetValue(id, out var value)) return null;
            if (value.Version.ToString() != request.ExpectedVersion)
                return new GraphChange(request.OperationId, id, field, value.Version.ToString(), false);
            value.Attributes[field] = request.Value.Clone();
            value.Version++;
            var receipt = new GraphChange(request.OperationId, id, field, value.Version.ToString(), true);
            operations.Add(request.OperationId, receipt);
            return receipt;
        }
    }
}
