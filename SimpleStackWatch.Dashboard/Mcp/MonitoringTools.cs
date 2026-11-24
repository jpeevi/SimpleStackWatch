using System.ComponentModel;
using System.Text.Json;
using ModelContextProtocol.Server;
using MonitorDashboard.Services;

namespace MonitorDashboard.Mcp;

[McpServerToolType]
public sealed class MonitoringTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(Name = "get_overview", ReadOnly = true)]
    [Description("Returns the current health summary and statuses for configured monitoring targets.")]
    public static async Task<string> GetOverview(MonitoringService monitoring, CancellationToken cancellationToken)
        => JsonSerializer.Serialize(await monitoring.OverviewAsync(cancellationToken), JsonOptions);

    [McpServerTool(Name = "list_targets", ReadOnly = true)]
    [Description("Lists configured monitoring target IDs, names, environments, groups and kinds.")]
    public static string ListTargets(MonitoringService monitoring)
        => JsonSerializer.Serialize(monitoring.Targets, JsonOptions);

    [McpServerTool(Name = "get_target_status", ReadOnly = true)]
    [Description("Returns the latest recorded health status for one configured monitoring target.")]
    public static async Task<string> GetTargetStatus(
        [Description("Exact monitoring target ID from list_targets.")] string targetId,
        MonitoringService monitoring,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetId) || !monitoring.HasTarget(targetId))
            return "{\"error\":\"Unknown monitoring target.\"}";

        // Reuse the dashboard overview's stale-reading and status semantics.
        var snapshot = JsonSerializer.SerializeToElement(
            await monitoring.OverviewAsync(cancellationToken), JsonOptions);
        foreach (var target in snapshot.GetProperty("targets").EnumerateArray())
            if (string.Equals(target.GetProperty("id").GetString(), targetId, StringComparison.Ordinal))
                return target.GetRawText();

        return "{\"error\":\"Monitoring target is unavailable.\"}";
    }

    [McpServerTool(Name = "get_target_history", ReadOnly = true)]
    [Description("Returns up to 200 recent stored health checks for one monitoring target, newest first.")]
    public static async Task<string> GetTargetHistory(
        [Description("Exact monitoring target ID from list_targets.")] string targetId,
        [Description("Maximum number of records to return, 1-200.")] int limit,
        MonitoringService monitoring,
        MonitoringStore store,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(targetId) || !monitoring.HasTarget(targetId))
            return "{\"error\":\"Unknown monitoring target.\"}";

        var history = await store.ReadHistoryAsync(targetId, Math.Clamp(limit, 1, 200), cancellationToken);
        return JsonSerializer.Serialize(history, JsonOptions);
    }
}
