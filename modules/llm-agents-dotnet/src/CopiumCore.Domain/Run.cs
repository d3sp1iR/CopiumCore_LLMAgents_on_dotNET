namespace CopiumCore.Domain;

public enum RunStatus
{
    Queued,
    Running,
    Succeeded,
    Failed,
    Cancelled,
    TimedOut
}

public sealed class Run
{
    public string Id { get; }
    public string AgentId { get; }
    public string AgentVersion { get; }
    public string UserId { get; }
    public string SessionId { get; }
    public string TaskId { get; }
    public DateTimeOffset CreatedAt { get; }
    public DateTimeOffset Deadline { get; }
    public RunStatus Status { get; private set; } = RunStatus.Queued;

    public Run(string id, string agentId, string agentVersion, string userId, string sessionId,
        string taskId, DateTimeOffset createdAt, DateTimeOffset deadline)
    {
        Id = RequireId(id, nameof(id));
        AgentId = RequireId(agentId, nameof(agentId));
        AgentVersion = RequireId(agentVersion, nameof(agentVersion));
        UserId = RequireId(userId, nameof(userId));
        SessionId = RequireId(sessionId, nameof(sessionId));
        TaskId = RequireId(taskId, nameof(taskId));
        if (deadline <= createdAt)
        {
            throw new ArgumentOutOfRangeException(nameof(deadline), "The deadline must be later than creation time.");
        }

        CreatedAt = createdAt.ToUniversalTime();
        Deadline = deadline.ToUniversalTime();
    }

    public void TransitionTo(RunStatus next)
    {
        var allowed = Status switch
        {
            RunStatus.Queued => next is RunStatus.Running or RunStatus.Cancelled or RunStatus.TimedOut,
            RunStatus.Running => next is RunStatus.Succeeded or RunStatus.Failed or RunStatus.Cancelled or RunStatus.TimedOut,
            _ => false
        };

        if (!allowed)
        {
            throw new InvalidOperationException($"Cannot transition run from {Status} to {next}.");
        }

        Status = next;
    }

    private static string RequireId(string value, string parameterName) =>
        !string.IsNullOrWhiteSpace(value) ? value : throw new ArgumentException("A non-empty value is required.", parameterName);
}
