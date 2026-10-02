using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MonitorDashboard.Models;
namespace MonitorDashboard.Services;
public sealed record ProcessRow(int Pid, string Name, double? CpuPercent, long? MemoryBytes, int? Threads);
public sealed class ProcessService(DashboardOptions options)
{
    private readonly SemaphoreSlim sampleGate = new(Math.Clamp(options.Processes.MaximumConcurrentSamples, 1, 4));
    private readonly Dictionary<int, (long Start, TimeSpan Cpu, DateTime At)> previous = new();
    public async Task<IReadOnlyList<ProcessRow>> ListAsync(CancellationToken ct)
    {
        // Only enumerate processes exposed through the .NET runtime diagnostics transport.
        var output = await RunAsync(["ps"], ct);
        var rows = new List<ProcessRow>();
        foreach (var line in output.Split('\n'))
        {
            var match = Regex.Match(line, @"^\s*(\d+)\s+(\S+)");
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out var pid) || !Allowed(pid)) continue;
            try
            {
                using var process = Process.GetProcessById(pid);
                var at = DateTime.UtcNow;
                var start = process.StartTime.ToUniversalTime().Ticks;
                var cpu = process.TotalProcessorTime;
                double? percent = null;
                lock (previous)
                {
                    if (previous.TryGetValue(pid, out var last) && last.Start == start && at > last.At)
                        percent = Math.Max(0, (cpu - last.Cpu).TotalSeconds / (at - last.At).TotalSeconds * 100);
                    previous[pid] = (start, cpu, at);
                }
                rows.Add(new(pid, match.Groups[2].Value, percent, process.WorkingSet64, process.Threads.Count));
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            { rows.Add(new(pid, match.Groups[2].Value, null, null, null)); }
        }
        lock (previous)
            foreach (var pid in previous.Keys.Except(rows.Select(r => r.Pid)).ToArray()) previous.Remove(pid);
        return rows;
    }
    public async Task EnsureAccessibleAsync(int pid, CancellationToken ct)
    {
        if (pid <= 0 || !Allowed(pid) || !(await ListAsync(ct)).Any(p => p.Pid == pid))
            throw new InvalidOperationException("Process is not accessible or is outside the configured allowlist.");
    }
    public async Task<string> CountersAsync(int pid, CancellationToken ct)
    {
        await EnsureAccessibleAsync(pid, ct);
        if (!await sampleGate.WaitAsync(0, ct)) throw new InvalidOperationException("Another counter sample is in progress. Try again shortly.");
        var directory = Path.Combine(Path.GetTempPath(), "monitor-dashboard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        if (OperatingSystem.IsLinux()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        try
        {
            var path = Path.Combine(directory, "counters.csv");
            var duration = TimeSpan.FromSeconds(Math.Clamp(options.Processes.CounterSampleSeconds, 3, 10)).ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);
            await RunAsync(["collect", "--process-id", pid.ToString(CultureInfo.InvariantCulture), "--counters", "System.Runtime", "--format", "csv", "--output", path, "--duration", duration], ct);
            if (!File.Exists(path)) throw new InvalidOperationException("The counter tool did not create an output file.");
            if (new FileInfo(path).Length > 2 * 1024 * 1024) throw new InvalidOperationException("Counter output exceeded the size limit.");
            return await File.ReadAllTextAsync(path, ct);
        }
        finally
        {
            try { Directory.Delete(directory, true); }
            finally { sampleGate.Release(); }
        }
    }
    private bool Allowed(int pid) => options.Processes.AllowedProcessIds.Length == 0 || options.Processes.AllowedProcessIds.Contains(pid);
    private async Task<string> RunAsync(IEnumerable<string> arguments, CancellationToken ct)
    {
        var start = new ProcessStartInfo(options.Processes.CountersExecutable) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet-counters.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Processes.CommandTimeoutSeconds, 15, 60)));
        var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var output = await stdout;
            await stderr; // Drain both streams without exposing arbitrary diagnostic error text to the browser.
            if (process.ExitCode != 0) throw new InvalidOperationException("dotnet-counters failed. Check runtime diagnostic permissions and tool configuration.");
            return output;
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            try { await Task.WhenAll(stdout, stderr); } catch { }
            throw;
        }
    }
    private static async Task<string> ReadBoundedAsync(StreamReader reader, CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);
            if (count == 0) return text.ToString();
            if (text.Length + count > 2 * 1024 * 1024) throw new InvalidOperationException("Diagnostic tool output exceeded the size limit.");
            text.Append(buffer, 0, count);
        }
    }
}
