using System.Text.Json;
using CopiumCore.AgentRuntime;

namespace CopiumCore.Api;

public sealed class LocalAgentExecutor(AgentExecutor runtime) : IAgentExecutor
{
    public async Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        var result = await runtime.ExecuteAsync(new CopiumCore.AgentRuntime.ExecutionRequest(request.Input, request.UserId, request.RunId), cancellationToken);
        var evidence = result.Evidence.Select(x => new SourceRef(x.ObjectId, x.Version, x.Field)).ToArray();
        PreparedProposal[] proposals = result.Proposal is { } p
            ? [new PreparedProposal(p.Id, p.ObjectId, p.Field, p.Before, JsonSerializer.SerializeToElement(p.After),
                "Значение указано в решении комиссии.", evidence)]
            : [];
        return new ExecutionResult(result.Outcome, result.Summary, evidence, proposals);
    }
}
