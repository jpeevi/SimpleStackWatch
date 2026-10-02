using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using MonitorDashboard.Models;

namespace MonitorDashboard.Services;

public sealed record ProcessRow(
    int Pid,
    string Name,
    double? CpuPercent,
    long? MemoryBytes,
    int? Threads);

public sealed class ProcessService(DashboardOptions options)
{
    private readonly SemaphoreSlim sampleGate =
        new(Math.Clamp(options.Processes.MaximumConcurrentSamples, 1, 4));

    private readonly Dictionary<int, (long Start, TimeSpan Cpu, DateTime At)> previous = new();

    public async Task<IReadOnlyList<ProcessRow>> ListAsync(CancellationToken ct)
    {
        // Discover only processes accessible through .NET runtime diagnostics.
        var output = await RunAsync(["ps"], ct);
        var rows = new List<ProcessRow>();

        foreach (var line in output.Split('\n'))
        {
            ct.ThrowIfCancellationRequested();

            var match = Regex.Match(line, @"^\s*(\d+)\s+(\S+)");

            if (!match.Success ||
                !int.TryParse(match.Groups[1].Value, out var pid) ||
                !Allowed(pid))
                continue;

            // Resolve the application name using Linux ps without changing the PID.
            var name = await ResolveApplicationNameAsync(
                pid, match.Groups[2].Value, ct);

            try
            {
                using var process = Process.GetProcessById(pid);

                var at = DateTime.UtcNow;
                var start = process.StartTime.ToUniversalTime().Ticks;
                var cpu = process.TotalProcessorTime;
                double? percent = null;

                lock (previous)
                {
                    if (previous.TryGetValue(pid, out var last) &&
                        last.Start == start &&
                        at > last.At)
                        percent = Math.Max(
                            0,
                            (cpu - last.Cpu).TotalSeconds /
                            (at - last.At).TotalSeconds * 100);

                    previous[pid] = (start, cpu, at);
                }

                rows.Add(new ProcessRow(
                    pid, name, percent,
                    process.WorkingSet64, process.Threads.Count));
            }
            catch (Exception ex) when (
                ex is ArgumentException or
                    InvalidOperationException or
                    Win32Exception)
            {
                rows.Add(new ProcessRow(pid, name, null, null, null));
            }
        }

        lock (previous)
        {
            foreach (var pid in previous.Keys
                         .Except(rows.Select(row => row.Pid))
                         .ToArray())
                previous.Remove(pid);
        }

        return rows;
    }

    private static async Task<string> ResolveApplicationNameAsync(
        int pid,
        string fallback,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return fallback;

        try
        {
            // Read the process command line without invoking a shell.
            var output = await RunToolAsync(
                "ps",
                [
                    "-ww",
                    "-p",
                    pid.ToString(CultureInfo.InvariantCulture),
                    "-o",
                    "pid=,user=,args="
                ],
                TimeSpan.FromSeconds(3),
                ct);

            var row = Regex.Match(
                output.Trim(),
                @"^\s*(\d+)\s+\S+\s+(.+)$");

            if (!row.Success ||
                !int.TryParse(row.Groups[1].Value, out var returnedPid) ||
                returnedPid != pid)
                return fallback;

            var commandLine = row.Groups[2].Value;

            if (Regex.IsMatch(commandLine, @"^(?:\S*/)?dotnet(?:\s|$)"))
            {
                // dotnet can host both .dll applications and managed .exe tools.
                var assembly = Regex.Match(
                    commandLine,
                    @"(?:^|\s)(?:""([^""]+\.(?:dll|exe))""|'([^']+\.(?:dll|exe))'|(\S+\.(?:dll|exe)))(?=\s|$)",
                    RegexOptions.IgnoreCase);

                if (!assembly.Success) return fallback;

                var path = assembly.Groups[1].Success
                    ? assembly.Groups[1].Value
                    : assembly.Groups[2].Success
                        ? assembly.Groups[2].Value
                        : assembly.Groups[3].Value;

                var name = Path.GetFileNameWithoutExtension(path);

                return string.IsNullOrWhiteSpace(name) ? fallback : name;
            }

            // Self-contained applications use their executable name.
            var executable = Regex.Match(
                commandLine,
                @"^(?:""([^""]+)""|'([^']+)'|(\S+))");

            if (!executable.Success) return fallback;

            var executablePath = executable.Groups[1].Success
                ? executable.Groups[1].Value
                : executable.Groups[2].Success
                    ? executable.Groups[2].Value
                    : executable.Groups[3].Value;

            var executableName = Path.GetFileName(executablePath);

            return string.IsNullOrWhiteSpace(executableName)
                ? fallback
                : executableName;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Keep the process list available if ps times out.
            return fallback;
        }
        catch (Exception ex) when (
            ex is ArgumentException or
                InvalidOperationException or
                Win32Exception or
                IOException or
                UnauthorizedAccessException)
        {
            return fallback;
        }
    }

    public async Task EnsureAccessibleAsync(int pid, CancellationToken ct)
    {
        if (pid <= 0 ||
            !Allowed(pid) ||
            !(await ListAsync(ct)).Any(process => process.Pid == pid))
            throw new InvalidOperationException(
                "Process is not accessible or is outside the configured allowlist.");
    }

    public async Task<string> CountersAsync(int pid, CancellationToken ct)
    {
        await EnsureAccessibleAsync(pid, ct);

        if (!await sampleGate.WaitAsync(0, ct))
            throw new InvalidOperationException(
                "Another counter sample is in progress. Try again shortly.");

        var directory = Path.Combine(
            Path.GetTempPath(),
            "monitor-dashboard-" + Guid.NewGuid().ToString("N"));

        try
        {
            Directory.CreateDirectory(directory);

            if (OperatingSystem.IsLinux())
                File.SetUnixFileMode(
                    directory,
                    UnixFileMode.UserRead |
                    UnixFileMode.UserWrite |
                    UnixFileMode.UserExecute);

            var path = Path.Combine(directory, "counters.csv");

            var duration = TimeSpan
                .FromSeconds(Math.Clamp(options.Processes.CounterSampleSeconds, 3, 10))
                .ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture);

            await RunAsync(
                [
                    "collect",
                    "--process-id", pid.ToString(CultureInfo.InvariantCulture),
                    "--counters", "System.Runtime",
                    "--format", "csv",
                    "--output", path,
                    "--duration", duration
                ],
                ct);

            if (!File.Exists(path))
                throw new InvalidOperationException(
                    "The counter tool did not create an output file.");

            if (new FileInfo(path).Length > 2 * 1024 * 1024)
                throw new InvalidOperationException(
                    "Counter output exceeded the size limit.");

            return await File.ReadAllTextAsync(path, ct);
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory)) Directory.Delete(directory, true);
            }
            finally
            {
                sampleGate.Release();
            }
        }
    }

    private bool Allowed(int pid)
    {
        return options.Processes.AllowedProcessIds.Length == 0 ||
               options.Processes.AllowedProcessIds.Contains(pid);
    }

    private Task<string> RunAsync(
        IEnumerable<string> arguments,
        CancellationToken ct)
    {
        return RunToolAsync(
            options.Processes.CountersExecutable,
            arguments,
            TimeSpan.FromSeconds(
                Math.Clamp(options.Processes.CommandTimeoutSeconds, 15, 60)),
            ct);
    }

    private static async Task<string> RunToolAsync(
        string executable,
        IEnumerable<string> arguments,
        TimeSpan commandTimeout,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments) start.ArgumentList.Add(argument);

        using var process = Process.Start(start)
                            ?? throw new InvalidOperationException(
                                "Could not start the diagnostic tool.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(commandTimeout);

        var stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token);
        var stderr = ReadBoundedAsync(process.StandardError, timeout.Token);

        try
        {
            await process.WaitForExitAsync(timeout.Token);

            var output = await stdout;

            // Drain errors without exposing command-line details to the browser.
            await stderr;

            if (process.ExitCode != 0)
                throw new InvalidOperationException(
                    "Diagnostic tool failed. Check runtime diagnostic permissions and tool configuration.");

            return output;
        }
        catch
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }
            catch (InvalidOperationException)
            {
                // The process may have exited before it could be terminated.
            }
            finally
            {
                try
                {
                    await Task.WhenAll(stdout, stderr);
                }
                catch
                {
                    // Preserve the original command failure.
                }
            }

            throw;
        }
    }

    private static async Task<string> ReadBoundedAsync(
        StreamReader reader,
        CancellationToken ct)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];

        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), ct);

            if (count == 0) return text.ToString();

            if (text.Length + count > 2 * 1024 * 1024)
                throw new InvalidOperationException(
                    "Diagnostic tool output exceeded the size limit.");

            text.Append(buffer, 0, count);
        }
    }
}