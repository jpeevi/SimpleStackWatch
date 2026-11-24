using MonitorDashboard.Models;

namespace MonitorDashboard.Services;

public sealed record MonitoringTarget(string Id, string Name, string Environment, string Group, string Kind);
public sealed record MonitoringReading(string Status, double? DurationMs, string Detail);

public static class MonitoringStatus
{
    public static MonitoringState Advance(
        string id, MonitoringState? previous, MonitoringReading reading,
        DateTimeOffset now, MonitoringOptions options)
    {
        var fail = previous?.Failures ?? 0;
        var recover = previous?.Recoveries ?? 0;
        string status;

        switch (reading.Status)
        {
            case "Healthy":
                fail = 0;
                recover++;
                status = previous?.Status is "Down" or "Degraded" &&
                    recover < Math.Clamp(options.RecoveryThreshold, 1, 20)
                    ? "Degraded" : "Healthy";
                break;
            case "Degraded":
                fail = 0;
                recover = 0;
                status = "Degraded";
                break;
            case "Down":
                fail++;
                recover = 0;
                status = fail >= Math.Clamp(options.FailureThreshold, 1, 20) ? "Down" : "Degraded";
                break;
            default:
                // No measurement must not fabricate a successful or failed check.
                status = "Unknown";
                fail = 0;
                recover = 0;
                break;
        }

        return new MonitoringState(id, status, fail, recover, now,
            reading.DurationMs, reading.Detail);
    }
}
