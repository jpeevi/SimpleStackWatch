using System.Diagnostics;
using System.Globalization;
using MonitorDashboard.Models;

namespace MonitorDashboard.Services;

public sealed class LinuxMonitoring
{
    private (long Idle, long Total)? previousCpu;

    public MonitoringReading Cpu(LinuxMonitoringOptions options)
    {
        if (!OperatingSystem.IsLinux()) return new("Unknown", null, "Linux checks require Linux.");
        var line = File.ReadLines("/proc/stat").First();
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts[0] != "cpu" || parts.Length < 5)
            return new("Unknown", null, "CPU counters unavailable.");
        var counts = parts.Skip(1).Select(x => long.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        var idle = counts[3] + (counts.Length > 4 ? counts[4] : 0);
        var total = counts.Sum();
        var previous = previousCpu;
        previousCpu = (idle, total);
        if (!previous.HasValue || total <= previous.Value.Total)
            return new("Unknown", null, "Collecting the initial CPU sample.");
        var usage = 100d * (1 - (double)(idle - previous.Value.Idle) / (total - previous.Value.Total));
        return Evaluate(usage, options, "CPU");
    }

    public static MonitoringReading Memory(LinuxMonitoringOptions options)
    {
        if (!OperatingSystem.IsLinux()) return new("Unknown", null, "Linux checks require Linux.");
        var values = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            var separator = line.IndexOf(':');
            if (separator < 0) continue;
            var key = line[..separator];
            if (key is not ("MemTotal" or "MemAvailable")) continue;
            var number = line[(separator + 1)..].Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
            values[key] = long.Parse(number, CultureInfo.InvariantCulture);
        }
        if (!values.TryGetValue("MemTotal", out var total) || total <= 0 ||
            !values.TryGetValue("MemAvailable", out var available))
            return new("Unknown", null, "Memory counters unavailable.");
        return Evaluate(100d * (total - available) / total, options, "Memory");
    }

    public static MonitoringReading Disk(string path, LinuxMonitoringOptions options)
    {
        if (!OperatingSystem.IsLinux()) return new("Unknown", null, "Linux checks require Linux.");
        var fullPath = Path.GetFullPath(path);
        if (!Directory.Exists(fullPath)) return new("Down", null, "Configured disk path is missing.");
        var mount = DriveInfo.GetDrives()
            .Where(d => fullPath == Path.TrimEndingDirectorySeparator(d.Name) ||
                fullPath.StartsWith(Path.TrimEndingDirectorySeparator(d.Name) + "/", StringComparison.Ordinal) ||
                d.Name == "/")
            .OrderByDescending(d => d.Name.Length)
            .FirstOrDefault();
        if (mount is null || !mount.IsReady || mount.TotalSize <= 0)
            return new("Unknown", null, "Disk metrics unavailable.");
        var usage = 100d * (mount.TotalSize - mount.AvailableFreeSpace) / mount.TotalSize;
        return Evaluate(usage, options, "Disk");
    }

    public static async Task<MonitoringReading> SystemdAsync(string name, CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return new("Unknown", null, "Systemd requires Linux.");
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = "systemctl",
            RedirectStandardOutput = true,
            RedirectStandardError = false,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        process.StartInfo.ArgumentList.Add("is-active");
        process.StartInfo.ArgumentList.Add("--");
        process.StartInfo.ArgumentList.Add(name);
        if (!process.Start()) return new("Down", null, "Unable to check the configured service.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var active = process.ExitCode == 0 && output.Trim() == "active";
            return new(active ? "Healthy" : "Down", null,
                active ? "Service active." : "Service is not active.");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            process.Kill(entireProcessTree: true);
            return new("Down", null, "Service check timed out.");
        }
    }

    private static MonitoringReading Evaluate(double value, LinuxMonitoringOptions options, string name)
    {
        var status = value >= options.CriticalPercent ? "Down" :
            value >= options.WarningPercent ? "Degraded" : "Healthy";
        return new(status, null, $"{name} usage {value:F1}%.");
    }
}
