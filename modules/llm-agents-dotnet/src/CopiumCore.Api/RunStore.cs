using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace CopiumCore.Api;

public interface IRunStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task SaveSessionAsync(DemoSession session, CancellationToken cancellationToken = default);
    Task SaveRunAsync(RunEntry run, CancellationToken cancellationToken = default);
    Task<RunView?> GetRunAsync(string id, CancellationToken cancellationToken = default);
    Task RecoverInterruptedAsync(CancellationToken cancellationToken = default);
}

public sealed class SqliteRunStore(IConfiguration configuration) : IRunStore
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
        var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
              id TEXT PRIMARY KEY, agent_id TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS runs (
              id TEXT PRIMARY KEY, agent_id TEXT NOT NULL, session_id TEXT NOT NULL,
              task_id TEXT NOT NULL, input TEXT NOT NULL, status TEXT NOT NULL,
              deadline TEXT, result_json TEXT, error_code TEXT);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        var columns = connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(runs)";
        var hasDeadline = false;
        await using (var reader = await columns.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
                if (string.Equals(reader.GetString(1), "deadline", StringComparison.OrdinalIgnoreCase)) hasDeadline = true;
        }
        if (!hasDeadline)
        {
            var migrate = connection.CreateCommand();
            migrate.CommandText = "ALTER TABLE runs ADD COLUMN deadline TEXT";
            await migrate.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public async Task SaveSessionAsync(DemoSession session, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "INSERT OR IGNORE INTO sessions(id,agent_id) VALUES($id,$agent)";
        command.Parameters.AddWithValue("$id", session.SessionId);
        command.Parameters.AddWithValue("$agent", session.AgentId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SaveRunAsync(RunEntry run, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO runs(id,agent_id,session_id,task_id,input,status,deadline,result_json,error_code)
            VALUES($id,$agent,$session,$task,$input,$status,$deadline,$result,$error)
            ON CONFLICT(id) DO UPDATE SET status=$status,deadline=$deadline,result_json=$result,error_code=$error;
            """;
        command.Parameters.AddWithValue("$id", run.Id);
        command.Parameters.AddWithValue("$agent", run.Request.AgentId);
        command.Parameters.AddWithValue("$session", run.Request.SessionId);
        command.Parameters.AddWithValue("$task", run.Request.TaskId);
        command.Parameters.AddWithValue("$input", run.Request.Input);
        command.Parameters.AddWithValue("$status", run.Status);
        command.Parameters.AddWithValue("$deadline", (object?)run.Deadline?.ToString("O") ?? DBNull.Value);
        command.Parameters.AddWithValue("$result", (object?)JsonSerializer.Serialize(run.Result) ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)run.ErrorCode ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "UPDATE runs SET status='failed',error_code='RUN_INTERRUPTED' WHERE status IN ('queued','running')";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<RunView?> GetRunAsync(string id, CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = await OpenAsync(cancellationToken);
        var command = connection.CreateCommand();
        command.CommandText = "SELECT id,status,result_json,error_code FROM runs WHERE id=$id";
        command.Parameters.AddWithValue("$id", id);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return null;
        var result = reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<RunResult>(reader.GetString(2));
        return new RunView(reader.GetString(0), reader.GetString(1), result, reader.IsDBNull(3) ? null : reader.GetString(3));
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }
}
