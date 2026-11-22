using System.Text.Json;

namespace CopiumCore.Api;

public sealed record AgentCreate(string Name, string Purpose, string ModelProfile, string SystemInstruction,
    string[] ToolAllowlist, JsonElement MemoryPolicy, JsonElement AccessPolicy, bool Enabled);
public sealed record AgentDefinition(string Id, string Version, string Name, string Purpose, string ModelProfile,
    string SystemInstruction, string[] ToolAllowlist, JsonElement MemoryPolicy, JsonElement AccessPolicy, bool Enabled);
public sealed record SessionCreate(string AgentId);
public sealed record RunCreate(string AgentId, string SessionId, string TaskId, string Input, int? TimeoutMs = null);
public sealed record SourceRef(string ObjectId, string Version, string? Field = null);
public sealed record RunResult(string Outcome, string Summary, SourceRef[] Evidence, string[] ProposalIds);
public sealed record PreparedProposal(string Id, string TargetObjectId, string Field, JsonElement Before, JsonElement After, string Reason, SourceRef[] Evidence);
public sealed record ExecutionResult(string Outcome, string Summary, SourceRef[] Evidence, PreparedProposal[] Proposals);
public sealed record ProposalView(string Id, string RunId, string Status, string TargetObjectId, string Field, JsonElement Before, JsonElement After, string Reason, SourceRef[] Evidence, int Version = 1);

// Implementations must respect cancellation and return only results they can substantiate.
public interface IAgentExecutor
{
    Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken);
}

public sealed record ExecutionRequest(string RunId, string UserId, string SessionId, string TaskId,
    string Input, AgentDefinition Agent);

public sealed class DemoAgentExecutor : IAgentExecutor
{
    public Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ExecutionResult("insufficientEvidence",
            "Demonstration executor: no model or evidence is connected.", [], []));
    }
}
