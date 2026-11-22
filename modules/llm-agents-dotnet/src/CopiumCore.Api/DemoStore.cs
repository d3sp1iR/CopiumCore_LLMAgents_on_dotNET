namespace CopiumCore.Api;

public sealed record DemoSession(string SessionId, string AgentId);
public sealed record RunView(string RunId, string Status, RunResult? Result, string? ErrorCode);

public sealed class RunEntry(string id, RunCreate request, AgentDefinition agent)
{
    public string Id { get; } = id;
    public RunCreate Request { get; } = request;
    public AgentDefinition Agent { get; } = agent;
    public string Status { get; set; } = "queued";
    public RunResult? Result { get; set; }
    public string? ErrorCode { get; set; }
}

public sealed class DemoStore(ILogger<DemoStore> logger)
{
    private readonly object gate = new();
    private readonly Dictionary<string, AgentDefinition> agents = [];
    private readonly Dictionary<string, DemoSession> sessions = [];
    private readonly Dictionary<string, RunEntry> runs = [];
    private readonly Dictionary<string, RunEntry> keys = [];
    private readonly Dictionary<string, ProposalView> proposals = [];

    public AgentDefinition AddAgent(AgentCreate request)
    {
        var agent = new AgentDefinition(Guid.NewGuid().ToString("N"), "1", request.Name, request.Purpose,
            request.ModelProfile, request.SystemInstruction, request.ToolAllowlist, request.MemoryPolicy.Clone(), request.AccessPolicy.Clone(), request.Enabled);
        lock (gate) agents.Add(agent.Id, agent);
        return agent;
    }

    public AgentDefinition[] GetAgents() { lock (gate) return agents.Values.ToArray(); }
    public AgentDefinition? GetAgent(string id) { lock (gate) return agents.GetValueOrDefault(id); }

    public DemoSession? AddSession(string agentId)
    {
        lock (gate)
        {
            if (!agents.TryGetValue(agentId, out var agent) || !agent.Enabled) return null;
            var session = new DemoSession(Guid.NewGuid().ToString("N"), agentId);
            sessions.Add(session.SessionId, session);
            return session;
        }
    }

    public (RunEntry? Entry, string? Error, bool Created) Accept(string key, RunCreate request)
    {
        lock (gate)
        {
            if (!sessions.TryGetValue(request.SessionId, out var session) || session.AgentId != request.AgentId ||
                !agents.TryGetValue(request.AgentId, out var agent) || !agent.Enabled) return (null, "NOT_FOUND", false);
            if (keys.TryGetValue(key, out var existing))
                return existing.Request == request ? (existing, null, false) : (null, "IDEMPOTENCY_CONFLICT", false);
            if (runs.Values.Any(r => r.Request.SessionId == request.SessionId && r.Status is "queued" or "running"))
                return (null, "SESSION_BUSY", false);
            var entry = new RunEntry(Guid.NewGuid().ToString("N"), request, agent);
            runs.Add(entry.Id, entry);
            keys.Add(key, entry);
            return (entry, null, true);
        }
    }

    public RunView? GetRun(string id)
    {
        lock (gate) return runs.TryGetValue(id, out var run) ? new(run.Id, run.Status, run.Result, run.ErrorCode) : null;
    }

    public ProposalView? GetProposal(string id)
    {
        lock (gate) return proposals.GetValueOrDefault(id);
    }

    private static bool ValidSource(SourceRef? source) => source is not null &&
        !string.IsNullOrWhiteSpace(source.ObjectId) && !string.IsNullOrWhiteSpace(source.Version);

    public async Task ExecuteAsync(RunEntry run, IAgentExecutor executor, CancellationToken cancellationToken, IReviewStore? reviewStore = null)
    {
        lock (gate) run.Status = "running";
        try
        {
            var result = await executor.ExecuteAsync(new(run.Id, "demo-user", run.Request.SessionId, run.Request.TaskId, run.Request.Input, run.Agent), cancellationToken);
            if (result is null || result.Evidence is null || result.Proposals is null || result.Summary is null ||
                result.Outcome is not ("answered" or "insufficientEvidence" or "proposalCreated") ||
                (result.Outcome == "proposalCreated") != (result.Proposals.Length > 0) ||
                result.Evidence.Any(e => !ValidSource(e)) ||
                result.Proposals.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.TargetObjectId) ||
                    string.IsNullOrWhiteSpace(p.Field) || string.IsNullOrWhiteSpace(p.Reason) ||
                    p.Before.ValueKind == System.Text.Json.JsonValueKind.Undefined || p.After.ValueKind == System.Text.Json.JsonValueKind.Undefined ||
                    p.Evidence is null || p.Evidence.Length == 0 || p.Evidence.Any(e => !ValidSource(e) || !result.Evidence.Contains(e))) ||
                result.Proposals.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != result.Proposals.Length)
                throw new InvalidOperationException("Invalid result");
            ProposalView[] prepared;
            lock (gate)
            {
                if (result.Proposals.Any(p => proposals.ContainsKey(p.Id))) throw new InvalidOperationException("Duplicate proposal");
                prepared = result.Proposals.Select(p => new ProposalView(p.Id, run.Id, "pendingReview", p.TargetObjectId,
                    p.Field, p.Before.Clone(), p.After.Clone(), p.Reason, p.Evidence.ToArray())).ToArray();
                foreach (var proposal in prepared) proposals.Add(proposal.Id, proposal);
                run.Result = new RunResult(result.Outcome, result.Summary, result.Evidence.ToArray(), prepared.Select(p => p.Id).ToArray());
            }
            if (reviewStore is not null)
                foreach (var proposal in prepared) await reviewStore.SaveAsync(proposal, cancellationToken);
            lock (gate) run.Status = "succeeded";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (gate) { run.Status = "cancelled"; }
        }
        catch (Exception ex)
        {
            logger.LogError("Run {RunId} failed: {ErrorType}", run.Id, ex.GetType().Name);
            lock (gate) { run.Status = "failed"; run.ErrorCode = ex is InvalidDataException ? "INVALID_MODEL_OUTPUT" : "EXECUTION_FAILED"; }
        }
    }
}
