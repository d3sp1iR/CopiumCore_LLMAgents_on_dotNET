using System.Net;
using System.Net.Http.Json;
using CopiumCore.Application.Graph;

namespace CopiumCore.Infrastructure.Graph;

public sealed class HttpChangeWriter(HttpClient client) : IChangeWriter
{
    public async Task<ChangeReceipt?> SetAttributeAsync(ChangeRequest request, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ObjectId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Field);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.ExpectedVersion);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.OperationId);

        using var response = await client.PutAsJsonAsync(
            $"objects/{Uri.EscapeDataString(request.ObjectId)}/attributes/{Uri.EscapeDataString(request.Field)}",
            new { value = request.Value, expectedVersion = request.ExpectedVersion, operationId = request.OperationId },
            cancellationToken);
        if (response.StatusCode is HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed)
            return null;
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ChangeReceipt>(cancellationToken: cancellationToken);
    }
}
