using System.Text.Json;

namespace CopiumCore.Application.Graph;

public sealed record ChangeRequest(string ObjectId, string Field, JsonElement Value, string ExpectedVersion, string OperationId);
public sealed record ChangeReceipt(string OperationId, string ObjectId, string Field, string Version, bool Applied);

public interface IChangeWriter
{
    Task<ChangeReceipt?> SetAttributeAsync(ChangeRequest request, CancellationToken cancellationToken);
}
