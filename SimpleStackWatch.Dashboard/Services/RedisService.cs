using MonitorDashboard.Models;
using StackExchange.Redis;
namespace MonitorDashboard.Services;
public sealed class RedisService(DashboardOptions options) : IDisposable
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private ConnectionMultiplexer? connection;
    public async Task<object> StatsAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.Redis.ConnectionString)) return new { configured = false };
        await gate.WaitAsync(ct);
        try
        {
            if (connection is null)
            {
                var config = ConfigurationOptions.Parse(options.Redis.ConnectionString);
                config.AbortOnConnectFail = false;
                config.ConnectTimeout = 5000;
                config.AsyncTimeout = 5000;
                connection = await ConnectionMultiplexer.ConnectAsync(config);
            }
        }
        finally { gate.Release(); }
        var latency = await connection.GetDatabase().PingAsync().WaitAsync(ct);
        var nodes = new List<object>();
        foreach (var endpoint in connection.GetEndPoints())
        {
            var server = connection.GetServer(endpoint);
            var info = await server.InfoAsync().WaitAsync(ct);
            var wanted = new HashSet<string> { "redis_version", "uptime_in_seconds", "connected_clients", "blocked_clients", "used_memory_human", "maxmemory_human", "instantaneous_ops_per_sec", "keyspace_hits", "keyspace_misses", "evicted_keys", "role", "master_link_status", "connected_slaves", "total_commands_processed" };
            var values = info.SelectMany(g => g).Where(x => wanted.Contains(x.Key)).ToDictionary(x => x.Key, x => x.Value);
            nodes.Add(new { endpoint = endpoint.ToString(), values });
        }
        return new { configured = true, latencyMs = latency.TotalMilliseconds, nodes };
    }
    public void Dispose() { connection?.Dispose(); gate.Dispose(); }
}
