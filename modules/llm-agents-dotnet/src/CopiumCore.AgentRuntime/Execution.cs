using System.Collections.Concurrent;
using System.ComponentModel;
using System.Text.Json;
using CopiumCore.Application.Graph;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace CopiumCore.AgentRuntime;

public sealed record ExecutionRequest(string Input, string UserId, string CorrelationId);
public sealed record SourceRef(string ObjectId, string Version, string? Field = null);
public sealed record PreparedProposal(string Id, string Operation, string ObjectId, string ExpectedVersion,
    string Field, JsonElement Before, string After, SourceRef[] Evidence);
public sealed record ExecutionResult(string Outcome, string Summary, SourceRef[] Evidence, string[] ProposalIds,
    PreparedProposal? Proposal = null);
internal sealed record ProposalCandidate(string Operation, string ObjectId, string ExpectedVersion,
    string Field, string NewValue, string DecisionId, string DecisionVersion);

public interface IAgentExecutor
{
    Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken);
}

public sealed class AgentExecutor(IChatClient chatClient, IGraphReader graphReader) : IAgentExecutor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ExecutionResult> ExecuteAsync(ExecutionRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.Input);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.UserId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.CorrelationId);

        var reads = new ConcurrentBag<GraphObject>();
        var context = new GraphReadContext(request.UserId, request.CorrelationId);

        [Description("Read a metagraph object by its exact ID. Returns its attributes and version, or null if not found.")]
        async Task<GraphObject?> GetObject([Description("Exact object ID")] string objectId, CancellationToken cancellationToken)
        {
            var result = await graphReader.GetObjectAsync(objectId, context, cancellationToken);
            if (result is not null)
            {
                reads.Add(result);
            }
            return result;
        }

        var tool = AIFunctionFactory.Create(GetObject, "getObject", "Read a metagraph object by ID.");
        var agent = chatClient.AsAIAgent(
            instructions: "Use getObject to read the objects needed for the user's request. Never invent sources. " +
                "Return only JSON with outcome (answered, insufficientEvidence or proposalCreated), summary (string), " +
                "evidence (array of {objectId, version, field?}), proposalIds (always empty array). " +
                "For answered cite at least one object actually read. For a requested groupCode change, " +
                "read both the target application and the decision with getObject. If the decision is approved " +
                "and its applicationId matches the target, return outcome proposalCreated and proposalCandidate: " +
                "{operation: 'setAttribute', objectId: target ID, expectedVersion: target version, " +
                "field: 'groupCode', newValue: decision groupCode, decisionId: decision ID, decisionVersion: decision version}. " +
                "Cite both objects with their exact versions. Do not claim a supportedBy relation. " +
                "field in evidence, if present, must be an exact top-level attribute name. " +
                "If evidence is insufficient, return insufficientEvidence without a proposalCandidate.",
            name: "E1GraphReader", tools: [tool]);
        var session = await agent.CreateSessionAsync(cancellationToken);
        var prompt = request.Input;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var response = await agent.RunAsync(prompt, session, cancellationToken: cancellationToken);
            if (TryValidate(response.Text, reads, out var result))
            {
                return result!;
            }
            prompt = "Your previous response was invalid. Return only a JSON object matching the requested schema, " +
                "with evidence citing only objects and versions returned by getObject. For proposalCreated include " +
                "a proposalCandidate as instructed; proposalIds must be empty.";
        }
        throw new InvalidDataException("INVALID_MODEL_OUTPUT: the model did not return a valid grounded result.");
    }

    private static PreparedProposal? PrepareProposal(ProposalCandidate? candidate, SourceRef[] evidence,
        ConcurrentBag<GraphObject> reads)
    {
        if (candidate is null || candidate.Operation != "setAttribute" || candidate.Field != "groupCode" ||
            string.IsNullOrWhiteSpace(candidate.ObjectId) || string.IsNullOrWhiteSpace(candidate.ExpectedVersion) ||
            string.IsNullOrWhiteSpace(candidate.DecisionId) || string.IsNullOrWhiteSpace(candidate.DecisionVersion) ||
            string.IsNullOrWhiteSpace(candidate.NewValue) || candidate.ObjectId == candidate.DecisionId)
            return null;

        var target = reads.FirstOrDefault(o => o.Id == candidate.ObjectId && o.Version == candidate.ExpectedVersion);
        var decision = reads.FirstOrDefault(o => o.Id == candidate.DecisionId && o.Version == candidate.DecisionVersion);
        if (target is null || decision is null || target.Type != "Application" || decision.Type != "Decision" ||
            !target.Attributes.TryGetProperty("groupCode", out var before) ||
            (before.ValueKind != JsonValueKind.Null && before.ValueKind != JsonValueKind.String) ||
            !decision.Attributes.TryGetProperty("approved", out var approved) || approved.ValueKind != JsonValueKind.True ||
            !decision.Attributes.TryGetProperty("applicationId", out var applicationId) ||
            applicationId.ValueKind != JsonValueKind.String || applicationId.GetString() != target.Id ||
            !decision.Attributes.TryGetProperty("groupCode", out var groupCode) ||
            groupCode.ValueKind != JsonValueKind.String || groupCode.GetString() != candidate.NewValue ||
            !evidence.Any(e => e.ObjectId == target.Id && e.Version == target.Version) ||
            !evidence.Any(e => e.ObjectId == decision.Id && e.Version == decision.Version))
            return null;

        SourceRef[] sources = [new(target.Id, target.Version, "groupCode"),
            new(decision.Id, decision.Version, "groupCode"), new(decision.Id, decision.Version, "approved"),
            new(decision.Id, decision.Version, "applicationId")];
        return new PreparedProposal(Guid.NewGuid().ToString("N"), "setAttribute", target.Id, target.Version,
            "groupCode", before.Clone(), candidate.NewValue, sources);
    }

    private static bool TryValidate(string? text, ConcurrentBag<GraphObject> reads, out ExecutionResult? result)
    {
        result = null;
        try
        {
            using var document = JsonDocument.Parse(text ?? "");
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("outcome", out _) || !root.TryGetProperty("summary", out _) ||
                !root.TryGetProperty("evidence", out _) || !root.TryGetProperty("proposalIds", out _))
                return false;
            var candidate = root.Deserialize<ExecutionResult>(JsonOptions);
            if (candidate is null || candidate.Outcome is not ("answered" or "insufficientEvidence" or "proposalCreated") ||
                candidate.Summary is null || candidate.Evidence is null || candidate.ProposalIds is null ||
                candidate.ProposalIds.Length != 0 || (candidate.Outcome == "answered" && candidate.Evidence.Length == 0))
                return false;
            foreach (var source in candidate.Evidence)
            {
                if (source is null || string.IsNullOrWhiteSpace(source.ObjectId) || string.IsNullOrWhiteSpace(source.Version) ||
                    !reads.Any(item => item.Id == source.ObjectId && item.Version == source.Version &&
                        (source.Field is null || (source.Field.Length > 0 && item.Attributes.TryGetProperty(source.Field, out _)))))
                    return false;
            }
            if (candidate.Proposal is not null)
                return false;
            if (candidate.Outcome == "proposalCreated")
            {
                if (!root.TryGetProperty("proposalCandidate", out var proposalJson) ||
                    proposalJson.ValueKind != JsonValueKind.Object)
                    return false;
                var proposalCandidate = proposalJson.Deserialize<ProposalCandidate>(JsonOptions);
                var proposal = PrepareProposal(proposalCandidate, candidate.Evidence, reads);
                if (proposal is null)
                    return false;
                result = candidate with { Proposal = proposal, ProposalIds = [proposal.Id], Evidence = proposal.Evidence };
                return true;
            }
            if (root.TryGetProperty("proposalCandidate", out var unexpected) && unexpected.ValueKind != JsonValueKind.Null)
                return false;
            result = candidate;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
