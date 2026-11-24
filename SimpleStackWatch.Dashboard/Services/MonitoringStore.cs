using System.Globalization;
using Microsoft.Data.Sqlite;
using MonitorDashboard.Models;

namespace MonitorDashboard.Services;

public sealed record MonitoringState(
    string TargetId, string Status, int Failures, int Recoveries,
    DateTimeOffset CheckedAt, double? DurationMs, string Detail);

public sealed record MonitoringSample(
    string TargetId, string Status, DateTimeOffset CheckedAt,
    double? DurationMs, string Detail);

// A separate database preserves the existing Identity schema and account data.
public sealed class MonitoringStore(DashboardOptions options)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private string ConnectionString => new SqliteConnectionStringBuilder
    {
        DataSource = Path.Combine(options.DataDirectory, "monitoring.db"),
        Mode = SqliteOpenMode.ReadWriteCreate
    }.ToString();

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(ct);
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS monitoring_state (
                    target_id TEXT NOT NULL PRIMARY KEY,
                    status TEXT NOT NULL,
                    failures INTEGER NOT NULL,
                    recoveries INTEGER NOT NULL,
                    checked_at TEXT NOT NULL,
                    duration_ms REAL NULL,
                    detail TEXT NOT NULL
                );
                CREATE TABLE IF NOT EXISTS monitoring_sample (
                    id INTEGER PRIMARY KEY AUTOINCREMENT,
                    target_id TEXT NOT NULL,
                    status TEXT NOT NULL,
                    checked_at TEXT NOT NULL,
                    duration_ms REAL NULL,
                    detail TEXT NOT NULL
                );
                CREATE INDEX IF NOT EXISTS ix_monitoring_sample_target_time
                    ON monitoring_sample(target_id, checked_at DESC);
                CREATE INDEX IF NOT EXISTS ix_monitoring_sample_time
                    ON monitoring_sample(checked_at);
                """;
            await cmd.ExecuteNonQueryAsync(ct);
        }
        finally { gate.Release(); }

        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(
                Path.Combine(options.DataDirectory, "monitoring.db"),
                UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    public async Task<Dictionary<string, MonitoringState>> ReadStatesAsync(CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT target_id, status, failures, recoveries, checked_at, duration_ms, detail FROM monitoring_state";
            await using var reader = await command.ExecuteReaderAsync(ct);
            var states = new Dictionary<string, MonitoringState>(StringComparer.Ordinal);
            while (await reader.ReadAsync(ct))
            {
                var state = new MonitoringState(
                    reader.GetString(0), reader.GetString(1), reader.GetInt32(2), reader.GetInt32(3),
                    DateTimeOffset.Parse(reader.GetString(4), CultureInfo.InvariantCulture),
                    reader.IsDBNull(5) ? null : reader.GetDouble(5), reader.GetString(6));
                states[state.TargetId] = state;
            }
            return states;
        }
        finally { gate.Release(); }
    }

    public async Task SaveAsync(
        IReadOnlyList<MonitoringState> states, IReadOnlyList<MonitoringSample> samples,
        DateTimeOffset cutoff, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(ct);
            await using var transaction = await connection.BeginTransactionAsync(ct);

            foreach (var state in states)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO monitoring_state(target_id,status,failures,recoveries,checked_at,duration_ms,detail)
                    VALUES ($id,$status,$fail,$recover,$at,$duration,$detail)
                    ON CONFLICT(target_id) DO UPDATE SET
                        status=excluded.status,failures=excluded.failures,
                        recoveries=excluded.recoveries,checked_at=excluded.checked_at,
                        duration_ms=excluded.duration_ms,detail=excluded.detail
                    """;
                BindSample(command, state.TargetId, state.Status, state.CheckedAt, state.DurationMs, state.Detail);
                command.Parameters.AddWithValue("$fail", state.Failures);
                command.Parameters.AddWithValue("$recover", state.Recoveries);
                await command.ExecuteNonQueryAsync(ct);
            }

            foreach (var sample in samples)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = """
                    INSERT INTO monitoring_sample(target_id,status,checked_at,duration_ms,detail)
                    VALUES ($id,$status,$at,$duration,$detail)
                    """;
                BindSample(command, sample.TargetId, sample.Status, sample.CheckedAt, sample.DurationMs, sample.Detail);
                await command.ExecuteNonQueryAsync(ct);
            }

            await using (var prune = connection.CreateCommand())
            {
                prune.Transaction = (SqliteTransaction)transaction;
                prune.CommandText = "DELETE FROM monitoring_sample WHERE checked_at < $cutoff";
                prune.Parameters.AddWithValue("$cutoff", Format(cutoff));
                await prune.ExecuteNonQueryAsync(ct);
            }
            await transaction.CommitAsync(ct);
        }
        finally { gate.Release(); }
    }

    public async Task<IReadOnlyList<MonitoringSample>> ReadHistoryAsync(string id, int limit, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            await using var connection = new SqliteConnection(ConnectionString);
            await connection.OpenAsync(ct);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT target_id,status,checked_at,duration_ms,detail
                FROM monitoring_sample WHERE target_id=$id
                ORDER BY checked_at DESC, id DESC LIMIT $limit
                """;
            command.Parameters.AddWithValue("$id", id);
            command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
            await using var reader = await command.ExecuteReaderAsync(ct);
            var result = new List<MonitoringSample>();
            while (await reader.ReadAsync(ct))
                result.Add(new MonitoringSample(reader.GetString(0), reader.GetString(1),
                    DateTimeOffset.Parse(reader.GetString(2), CultureInfo.InvariantCulture),
                    reader.IsDBNull(3) ? null : reader.GetDouble(3), reader.GetString(4)));
            return result;
        }
        finally { gate.Release(); }
    }

    private static void BindSample(SqliteCommand command, string id, string status,
        DateTimeOffset checkedAt, double? durationMs, string detail)
    {
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$at", Format(checkedAt));
        command.Parameters.AddWithValue("$duration", (object?)durationMs ?? DBNull.Value);
        command.Parameters.AddWithValue("$detail", detail);
    }

    private static string Format(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
}
