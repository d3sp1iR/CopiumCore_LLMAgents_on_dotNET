using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CopiumCore.Api;

public sealed record ReviewDecision(string ProposalId, string Status, string? OperationId, string? Reason, int Version);
public sealed record OutboxEvent(string EventId, string Type, string AggregateId, string Payload, int Attempts, bool Published);

public interface IReviewStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(ProposalView proposal, CancellationToken cancellationToken = default);
    Task<ProposalView?> GetAsync(string id, CancellationToken cancellationToken = default);
    Task<ReviewDecision?> DecideAsync(string id, string decision, string? reason, int expectedVersion, string idempotencyKey, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<OutboxEvent>> PendingEventsAsync(CancellationToken cancellationToken = default);
    Task MarkPublishedAsync(string eventId, CancellationToken cancellationToken = default);
}

public sealed class SqliteReviewStore(IConfiguration configuration, ILogger<SqliteReviewStore> logger) : IReviewStore, IAsyncDisposable
{
    private readonly string connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = configuration["CopiumCore:SqlitePath"] ?? "copiumcore-e2.db",
        Mode = SqliteOpenMode.ReadWriteCreate,
        Cache = SqliteCacheMode.Shared
    }.ToString();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken);
        logger.LogDebug("SQLite review store initialized at {DataSource}", connection.DataSource);
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS proposals (
              id TEXT PRIMARY KEY, run_id TEXT NOT NULL, status TEXT NOT NULL,
              target_object_id TEXT NOT NULL, field TEXT NOT NULL, before_json TEXT NOT NULL,
              after_json TEXT NOT NULL, reason TEXT NOT NULL, evidence_json TEXT NOT NULL,
              version INTEGER NOT NULL DEFAULT 1, decision_reason TEXT, operation_id TEXT);
            CREATE TABLE IF NOT EXISTS outbox (
              event_id TEXT PRIMARY KEY, type TEXT NOT NULL, aggregate_id TEXT NOT NULL,
              payload TEXT NOT NULL, attempts INTEGER NOT NULL DEFAULT 0,
              published INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS idempotency (
              key TEXT PRIMARY KEY, proposal_id TEXT NOT NULL, decision TEXT NOT NULL,
              response_json TEXT NOT NULL);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveAsync(ProposalView proposal, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO proposals
            (id,run_id,status,target_object_id,field,before_json,after_json,reason,evidence_json)
            VALUES ($id,$run,$status,$target,$field,$before,$after,$reason,$evidence);
            INSERT OR IGNORE INTO outbox(event_id,type,aggregate_id,payload)
            VALUES ($event,$type,$id,$payload);
            """;
        command.Parameters.AddWithValue("$id", proposal.Id);
        command.Parameters.AddWithValue("$run", proposal.RunId);
        command.Parameters.AddWithValue("$status", proposal.Status);
        command.Parameters.AddWithValue("$target", proposal.TargetObjectId);
        command.Parameters.AddWithValue("$field", proposal.Field);
        command.Parameters.AddWithValue("$before", proposal.Before.GetRawText());
        command.Parameters.AddWithValue("$after", proposal.After.GetRawText());
        command.Parameters.AddWithValue("$reason", proposal.Reason);
        command.Parameters.AddWithValue("$evidence", JsonSerializer.Serialize(proposal.Evidence));
        command.Parameters.AddWithValue("$event", $"evt-{proposal.Id}");
        command.Parameters.AddWithValue("$type", "llm.proposal.created");
        command.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(new { proposalId = proposal.Id, runId = proposal.RunId }));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<ProposalView?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,run_id,status,target_object_id,field,before_json,after_json,reason,evidence_json FROM proposals WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        return ReadProposal(reader);
    }

    public async Task<ReviewDecision?> DecideAsync(string id, string decision, string? reason, int expectedVersion, string idempotencyKey, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
        var existing = connection.CreateCommand();
        existing.Transaction = transaction;
        existing.CommandText = "SELECT proposal_id,response_json FROM idempotency WHERE key=$key";
        existing.Parameters.AddWithValue("$key", idempotencyKey);
        await using (var replay = await existing.ExecuteReaderAsync(cancellationToken))
            if (await replay.ReadAsync(cancellationToken)) return JsonSerializer.Deserialize<ReviewDecision>(replay.GetString(1));

        var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = "UPDATE proposals SET status=$status,version=version+1,decision_reason=$reason,operation_id=$operation WHERE id=$id AND status='pendingReview' AND version=$version";
        update.Parameters.AddWithValue("$status", decision == "approve" ? "approved" : "rejected");
        update.Parameters.AddWithValue("$reason", (object?)reason ?? DBNull.Value);
        var operationId = decision == "approve" ? $"op-{Guid.NewGuid():N}" : null;
        update.Parameters.AddWithValue("$operation", (object?)operationId ?? DBNull.Value);
        update.Parameters.AddWithValue("$id", id);
        update.Parameters.AddWithValue("$version", expectedVersion);
        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1) return null;
        var result = new ReviewDecision(id, decision == "approve" ? "approved" : "rejected", operationId, reason, expectedVersion + 1);
        var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO idempotency(key,proposal_id,decision,response_json) VALUES($key,$id,$decision,$response); INSERT INTO outbox(event_id,type,aggregate_id,payload) VALUES($event,$type,$id,$payload);";
        insert.Parameters.AddWithValue("$key", idempotencyKey); insert.Parameters.AddWithValue("$id", id); insert.Parameters.AddWithValue("$decision", decision);
        insert.Parameters.AddWithValue("$response", JsonSerializer.Serialize(result)); insert.Parameters.AddWithValue("$event", $"evt-{id}-{result.Version}");
        insert.Parameters.AddWithValue("$type", decision == "approve" ? "llm.proposal.approved" : "llm.proposal.rejected"); insert.Parameters.AddWithValue("$payload", JsonSerializer.Serialize(result));
        await insert.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<OutboxEvent>> PendingEventsAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken); await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand(); command.CommandText = "SELECT event_id,type,aggregate_id,payload,attempts,published FROM outbox WHERE published=0 ORDER BY rowid";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken); var result = new List<OutboxEvent>();
        while (await reader.ReadAsync(cancellationToken)) result.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetInt32(4), reader.GetBoolean(5)));
        return result;
    }

    public async Task MarkPublishedAsync(string eventId, CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenAsync(cancellationToken); var command = connection.CreateCommand(); command.CommandText = "UPDATE outbox SET published=1,attempts=attempts+1 WHERE event_id=$id"; command.Parameters.AddWithValue("$id", eventId); await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken token) { var connection = new SqliteConnection(connectionString); await connection.OpenAsync(token); return connection; }
    private static ProposalView ReadProposal(SqliteDataReader reader) => new(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), JsonDocument.Parse(reader.GetString(5)).RootElement.Clone(), JsonDocument.Parse(reader.GetString(6)).RootElement.Clone(), reader.GetString(7), JsonSerializer.Deserialize<SourceRef[]>(reader.GetString(8)) ?? []);
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
