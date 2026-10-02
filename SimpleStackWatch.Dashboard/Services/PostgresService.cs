using Npgsql;
using MonitorDashboard.Models;
namespace MonitorDashboard.Services;
public sealed class PostgresService(DashboardOptions options)
{
    // Ignore connection strings in JSON/command-line configuration. Secrets come
    // directly from environment variables, indexed in the same order as the names.
    public static void Configure(PostgresOptions postgres, Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        if (postgres.Databases.Count == 0)
        {
            // Preserve the previous single-connection environment variable.
            var legacy = readEnvironment("Dashboard__Postgres__ConnectionString");
            if (!string.IsNullOrWhiteSpace(legacy))
                postgres.Databases.Add(new() { Name = "Default", ConnectionString = legacy });
            return;
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < postgres.Databases.Count; index++)
        {
            var database = postgres.Databases[index];
            database.Name = database.Name.Trim();
            if (database.Name.Length == 0 || !names.Add(database.Name))
                throw new InvalidOperationException("PostgreSQL database names must be nonempty and unique.");
            database.ConnectionString = readEnvironment($"Dashboard__Postgres__Databases__{index}__ConnectionString") ?? "";
        }
    }

    public bool HasDatabase(int index) => index >= 0 && index < options.Postgres.Databases.Count;

    // Explicit projection prevents secrets from being serialized into API responses.
    public object Databases() => options.Postgres.Databases.Select((database, index) => new
    {
        index, database.Name, configured = !string.IsNullOrWhiteSpace(database.ConnectionString)
    }).ToArray();

    public async Task<object> SessionsAsync(int index, CancellationToken ct)
    {
        if (!HasDatabase(index)) throw new ArgumentOutOfRangeException(nameof(index));
        var database = options.Postgres.Databases[index];
        if (string.IsNullOrWhiteSpace(database.ConnectionString)) return new { configured = false };
        await using var connection = new NpgsqlConnection(database.ConnectionString);
        await connection.OpenAsync(ct);
        var queryColumn = options.Postgres.ShowQueryText ? "left(query, 2000)" : "NULL::text";
        await using var command = new NpgsqlCommand($"""
            SELECT pid, usename, datname, application_name, client_addr::text, state,
                   wait_event_type, wait_event, EXTRACT(EPOCH FROM (clock_timestamp() - query_start))::double precision,
                   {queryColumn}
            FROM pg_stat_activity
            WHERE datname = current_database() AND pid <> pg_backend_pid()
            ORDER BY (state = 'active') DESC NULLS LAST, query_start ASC NULLS LAST LIMIT @limit
            """, connection) { CommandTimeout = 5 };
        command.Parameters.AddWithValue("limit", Math.Clamp(options.Postgres.MaximumRows, 1, 500));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var rows = new List<object>();
        while (await reader.ReadAsync(ct))
        {
            object? Cell(int n) => reader.IsDBNull(n) ? null : reader.GetValue(n);
            rows.Add(new { pid = Cell(0), user = Cell(1), database = Cell(2), application = Cell(3), client = Cell(4), state = Cell(5), waitType = Cell(6), wait = Cell(7), queryAgeSeconds = Cell(8), query = Cell(9) });
        }
        return new { configured = true, rows, queryTextEnabled = options.Postgres.ShowQueryText };
    }
}
