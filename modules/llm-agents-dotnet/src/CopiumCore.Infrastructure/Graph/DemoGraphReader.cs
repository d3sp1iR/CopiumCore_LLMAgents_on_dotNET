using System.Net;
using System.Net.Http.Json;
using CopiumCore.Application.Graph;

namespace CopiumCore.Infrastructure.Graph;

public sealed class DemoGraphReader(HttpClient client) : IGraphReader
{
    public async Task<GraphObject?> GetObjectAsync(string objectId, GraphReadContext context, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(objectId);
        if (objectId is "." or ".." || objectId.IndexOfAny(['/', '\\']) >= 0)
        {
            throw new ArgumentException("The object ID must be a single path segment.", nameof(objectId));
        }

        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(context.CorrelationId);

        using var request = new HttpRequestMessage(HttpMethod.Get, $"objects/{Uri.EscapeDataString(objectId)}");
        using var response = await client.SendAsync(request, cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var graphObject = await response.Content.ReadFromJsonAsync<GraphObject>(cancellationToken: cancellationToken);
        if (graphObject is null || string.IsNullOrWhiteSpace(graphObject.Id) ||
            string.IsNullOrWhiteSpace(graphObject.Type) || string.IsNullOrWhiteSpace(graphObject.Version) ||
            graphObject.Attributes.ValueKind != System.Text.Json.JsonValueKind.Object ||
            !string.Equals(graphObject.Id, objectId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The graph service returned an invalid object.");
        }

        return graphObject;
    }
}
