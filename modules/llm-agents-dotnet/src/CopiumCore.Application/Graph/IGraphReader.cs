using System.Text.Json;

namespace CopiumCore.Application.Graph;

public sealed record GraphObject(string Id, string Type, string Version, JsonElement Attributes);

public sealed record GraphReadContext(string UserId, string CorrelationId);

public interface IGraphReader
{
    Task<GraphObject?> GetObjectAsync(string objectId, GraphReadContext context, CancellationToken cancellationToken);
}
