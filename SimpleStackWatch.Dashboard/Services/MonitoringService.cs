using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MonitorDashboard.Models;

namespace MonitorDashboard.Services;

// The hosted poller and the authenticated API share this singleton.
public sealed class MonitoringService(
    DashboardOptions options,
    MonitoringStore store,
    IHttpClientFactory clients,
    HealthCheckService health,
    AwsService aws,
    ILogger<MonitoringService> logger) : BackgroundService
{
    private readonly LinuxMonitoring linux = new();
    private readonly IReadOnlyList<MonitoringTarget> targets = BuildTargets(options);
    private readonly Dictionary<string, MonitoringState> states = new(StringComparer.Ordinal);

    public static void Configure(MonitoringOptions settings, Func<string, string?>? readEnvironment = null)
    {
        readEnvironment ??= Environment.GetEnvironmentVariable;
        settings.Alerts.WebhookUrl = readEnvironment("Dashboard__Monitoring__Alerts__WebhookUrl") ?? "";
        if (!string.IsNullOrWhiteSpace(settings.Alerts.WebhookUrl) &&
            (!Uri.TryCreate(settings.Alerts.WebhookUrl, UriKind.Absolute, out var webhook) ||
             (webhook.Scheme != "https" && !(webhook.Scheme == "http" && webhook.IsLoopback)) ||
             !string.IsNullOrEmpty(webhook.UserInfo) || !string.IsNullOrEmpty(webhook.Fragment)))
            throw new InvalidOperationException("Monitoring alert webhook must be HTTPS (or loopback HTTP).");
        for (var i = 0; i < settings.Endpoints.Count; i++)
        {
            var endpoint = settings.Endpoints[i];
            var index = endpoint.ConfigurationIndex ?? i.ToString(System.Globalization.CultureInfo.InvariantCulture);
            endpoint.HeaderValue = readEnvironment($"Dashboard__Monitoring__Endpoints__{index}__HeaderValue") ?? "";
        }
    }

    public static IReadOnlyList<MonitoringTarget> BuildTargets(DashboardOptions options)
    {
        var settings = options.Monitoring;
        if (!settings.Enabled) return [];
        var list = new List<MonitoringTarget>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string id, string name, string environment, string group, string kind)
        {
            if (id.Length is < 1 or > 160 || !ids.Add(id) ||
                string.IsNullOrWhiteSpace(name) || name.Length > 120 ||
                string.IsNullOrWhiteSpace(environment) || environment.Length > 80 ||
                string.IsNullOrWhiteSpace(group) || group.Length > 80)
                throw new InvalidOperationException("Monitoring targets need unique IDs and valid names, environments and groups.");
            list.Add(new MonitoringTarget(id, name, environment, group, kind));
        }

        if (settings.MaxHttpEndpoints < 1)
            throw new InvalidOperationException("Monitoring MaxHttpEndpoints must be positive.");
        if (settings.Endpoints.Count > settings.MaxHttpEndpoints)
            throw new InvalidOperationException("Configured HTTP monitoring targets exceed MaxHttpEndpoints.");
        foreach (var endpoint in settings.Endpoints)
        {
            if (!Uri.TryCreate(endpoint.Url, UriKind.Absolute, out var uri) ||
                uri.Scheme is not ("http" or "https") ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Fragment) ||
                endpoint.ExpectedStatusCode is < 100 or > 599 ||
                endpoint.TimeoutSeconds is < 1 or > 60 ||
                (endpoint.HeaderName.Length > 0 &&
                    !Regex.IsMatch(endpoint.HeaderName, "^[A-Za-z0-9-]+$", RegexOptions.CultureInvariant)) ||
                endpoint.HeaderValue.Contains('\r') || endpoint.HeaderValue.Contains('\n'))
                throw new InvalidOperationException("A monitoring HTTP endpoint has invalid settings.");
            // Keep URLs out of the exposed ID; indexing/reordering must not change history.
            if (string.IsNullOrEmpty(endpoint.Id))
            {
                var identity = JsonSerializer.SerializeToUtf8Bytes(new[]
                    { endpoint.Name, endpoint.Environment, uri.AbsoluteUri });
                endpoint.Id = "http-" + Convert.ToHexString(SHA256.HashData(identity)).ToLowerInvariant();
            }
            if (!Regex.IsMatch(endpoint.Id, "^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant))
                throw new InvalidOperationException("A monitoring HTTP endpoint has an invalid ID.");
            Add(endpoint.Id, endpoint.Name, endpoint.Environment, endpoint.Group, "http");
        }

        if (settings.Linux.Enabled)
        {
            var host = settings.Linux;
            if (!OperatingSystem.IsLinux() ||
                host.WarningPercent is < 1 or > 99 ||
                host.CriticalPercent is < 2 or > 100 ||
                host.WarningPercent >= host.CriticalPercent)
                throw new InvalidOperationException("Linux monitoring requires Linux and valid usage thresholds.");
            Add("linux:cpu", "CPU", host.Environment, host.Group, "cpu");
            Add("linux:memory", "Memory", host.Environment, host.Group, "memory");
            foreach (var path in host.DiskPaths)
            {
                if (!Path.IsPathFullyQualified(path) || path.Length > 120)
                    throw new InvalidOperationException("Linux disk paths must be absolute and at most 120 characters.");
                Add("linux:disk:" + path, "Disk " + path, host.Environment, host.Group, "disk");
            }
            foreach (var service in host.Services)
            {
                if (!Regex.IsMatch(service, "^[A-Za-z0-9][A-Za-z0-9@_.:-]*$", RegexOptions.CultureInvariant) ||
                    service.Length > 100)
                    throw new InvalidOperationException("Invalid systemd service name.");
                Add("linux:service:" + service, service, host.Environment, host.Group, "systemd");
            }
        }

        for (var index = 0; index < options.Postgres.Databases.Count; index++)
        {
            var db = options.Postgres.Databases[index];
            if (!string.IsNullOrWhiteSpace(db.ConnectionString))
                Add($"postgres:{index}", db.Name, options.Postgres.Environment, options.Postgres.Group, "postgres");
        }
        if (!string.IsNullOrWhiteSpace(options.Redis.ConnectionString))
            Add("redis", "Redis", options.Redis.Environment, options.Redis.Group, "redis");
        if (options.Aws.Enabled)
            Add("aws:ec2", "EC2 instances", options.Aws.Environment, options.Aws.Group, "ec2");
        return list;
    }

    public static bool IsHttpStatusHealthy(HttpTargetOptions endpoint, HttpStatusCode status)
    {
        var code = (int)status;
        return endpoint.ReachabilityOnly ? code is >= 100 and < 500 : code == endpoint.ExpectedStatusCode;
    }

    public IReadOnlyList<MonitoringTarget> Targets => targets;

    public bool HasTarget(string id) => targets.Any(t => t.Id == id);

    public async Task<object> OverviewAsync(CancellationToken ct)
    {
        var saved = await store.ReadStatesAsync(ct);
        var staleAfter = TimeSpan.FromSeconds(Math.Max(90, Math.Clamp(options.Monitoring.PollSeconds, 5, 300) * 3));
        var now = DateTimeOffset.UtcNow;
        var rows = targets.Select(target =>
        {
            saved.TryGetValue(target.Id, out var latest);
            var stale = latest is null || now - latest.CheckedAt > staleAfter;
            return new
            {
                target.Id, target.Name, target.Environment, target.Group, target.Kind,
                status = stale ? "Unknown" : latest!.Status,
                checkedAt = latest?.CheckedAt,
                durationMs = latest?.DurationMs,
                detail = stale ? "Waiting for a recent check." : latest!.Detail
            };
        }).ToArray();
        return new
        {
            enabled = options.Monitoring.Enabled,
            pollSeconds = Math.Clamp(options.Monitoring.PollSeconds, 5, 300),
            summary = new
            {
                total = rows.Length,
                healthy = rows.Count(r => r.status == "Healthy"),
                degraded = rows.Count(r => r.status == "Degraded"),
                down = rows.Count(r => r.status == "Down"),
                unknown = rows.Count(r => r.status == "Unknown")
            },
            targets = rows
        };
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Monitoring.Enabled) return;
        try
        {
            foreach (var (id, value) in await store.ReadStatesAsync(stoppingToken))
                states[id] = value;

            // Run once on startup; no overlapping timer iterations.
            await PollSafeAsync(stoppingToken);
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(
                Math.Clamp(options.Monitoring.PollSeconds, 5, 300)));
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await PollSafeAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }

    private async Task PollSafeAsync(CancellationToken ct)
    {
        try { await PollAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            // Do not log exception messages: connection strings or URLs may contain secrets.
            logger.LogWarning("Monitoring poll failed ({Type}).", exception.GetType().Name);
        }
    }

    private async Task PollAsync(CancellationToken ct)
    {
        using var semaphore = new SemaphoreSlim(Math.Clamp(options.Monitoring.MaxConcurrentChecks, 1, 16));
        var readings = await Task.WhenAll(targets.Select(async target =>
        {
            await semaphore.WaitAsync(ct);
            try
            {
                var clock = Stopwatch.StartNew();
                MonitoringReading reading;
                try { reading = await CheckAsync(target, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    reading = new MonitoringReading("Down", null,
                        $"Check unavailable ({exception.GetType().Name}).");
                }
                return (target, reading with { DurationMs = reading.DurationMs ?? clock.Elapsed.TotalMilliseconds });
            }
            finally { semaphore.Release(); }
        }));

        var samples = new List<MonitoringSample>();
        var updates = new List<MonitoringState>();
        var alerts = new List<(MonitoringTarget Target, MonitoringState State, string Previous)>();
        foreach (var (target, reading) in readings)
        {
            states.TryGetValue(target.Id, out var previous);
            var now = DateTimeOffset.UtcNow;
            var next = MonitoringStatus.Advance(target.Id, previous, reading, now, options.Monitoring);
            updates.Add(next);
            samples.Add(new MonitoringSample(target.Id, next.Status, now, next.DurationMs, next.Detail));
            if (previous is not null &&
                ((next.Status == "Down" && previous.Status != "Down") ||
                 (next.Status == "Healthy" && previous.Status is "Down" or "Degraded")))
                alerts.Add((target, next, previous.Status));
        }

        await store.SaveAsync(updates, samples,
            DateTimeOffset.UtcNow.AddDays(-Math.Clamp(options.Monitoring.RetentionDays, 1, 365)), ct);
        foreach (var state in updates) states[state.TargetId] = state;
        // Alert deliveries must not serialize dozens of five-second timeouts.
        using var alertGate = new SemaphoreSlim(4);
        await Task.WhenAll(alerts.Select(async alert =>
        {
            await alertGate.WaitAsync(ct);
            try { await NotifyAsync(alert.Target, alert.State, alert.Previous, ct); }
            finally { alertGate.Release(); }
        }));
    }

    private async Task<MonitoringReading> CheckAsync(MonitoringTarget target, CancellationToken ct)
    {
        switch (target.Kind)
        {
            case "http":
            {
                var endpoint = options.Monitoring.Endpoints.First(e => e.Id == target.Id);
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint.Url);
                if (!string.IsNullOrEmpty(endpoint.HeaderName) && !string.IsNullOrEmpty(endpoint.HeaderValue))
                    request.Headers.TryAddWithoutValidation(endpoint.HeaderName, endpoint.HeaderValue);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(TimeSpan.FromSeconds(endpoint.TimeoutSeconds));
                try
                {
                    var client = clients.CreateClient("health-targets");
                    using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    var success = IsHttpStatusHealthy(endpoint, response.StatusCode);
                    var statusCode = (int)response.StatusCode;
                    var detail = endpoint.ReachabilityOnly
                        ? $"HTTP {statusCode} ({(success ? "endpoint reachable" : "server error")})."
                        : $"HTTP {statusCode} (expected {endpoint.ExpectedStatusCode}).";
                    return new(success ? "Healthy" : "Down", null, detail);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    return new("Down", null, "HTTP check timed out.");
                }
            }
            case "cpu": return linux.Cpu(options.Monitoring.Linux);
            case "memory": return LinuxMonitoring.Memory(options.Monitoring.Linux);
            case "disk": return LinuxMonitoring.Disk(target.Id["linux:disk:".Length..], options.Monitoring.Linux);
            case "systemd": return await LinuxMonitoring.SystemdAsync(target.Id["linux:service:".Length..], ct);
            case "postgres":
            case "redis":
            {
                var report = await health.CheckHealthAsync(r => r.Tags.Contains(target.Id), ct);
                if (report.Entries.Count == 0) return new("Unknown", null, "Health check unavailable.");
                return new(report.Status == HealthStatus.Healthy ? "Healthy" :
                    report.Status == HealthStatus.Degraded ? "Degraded" : "Down",
                    report.TotalDuration.TotalMilliseconds,
                    report.Status == HealthStatus.Healthy ? "Dependency connected." : "Dependency health check failed.");
            }
            case "ec2": return await AwsHealthAsync(ct);
            default: return new("Unknown", null, "Unsupported check type.");
        }
    }

    private async Task<MonitoringReading> AwsHealthAsync(CancellationToken ct)
    {
        // Reuse the existing EC2/CloudWatch integration and its one-minute cache.
        var payload = await aws.InstancesAsync(ct);
        var root = JsonSerializer.SerializeToElement(payload);
        if (!root.TryGetProperty("instances", out var instances))
            return new("Unknown", null, "EC2 integration is not configured.");
        var count = 0;
        var failed = 0;
        var unknown = 0;
        foreach (var instance in instances.EnumerateArray())
        {
            count++;
            if (!instance.TryGetProperty("state", out var state) || state.GetString() != "running")
            {
                failed++;
                continue;
            }
            if (!instance.TryGetProperty("metrics", out var metrics) ||
                !metrics.TryGetProperty("StatusCheckFailed", out var check) ||
                check.ValueKind == JsonValueKind.Null ||
                !check.TryGetProperty("value", out var value) ||
                value.ValueKind != JsonValueKind.Number)
            {
                unknown++;
                continue;
            }
            if (value.GetDouble() > 0) failed++;
        }
        if (count == 0) return new("Unknown", null, "No EC2 instances were returned.");
        return new(failed > 0 ? "Down" : unknown > 0 ? "Degraded" : "Healthy", null,
            $"EC2 instances: {count} total, {failed} failing, {unknown} without status datapoints.");
    }

    private async Task NotifyAsync(MonitoringTarget target, MonitoringState state,
        string previous, CancellationToken ct)
    {
        var url = options.Monitoring.Alerts.WebhookUrl;
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            using var response = await clients.CreateClient("monitoring-alerts").PostAsJsonAsync(
                url, new
                {
                    targetId = target.Id, target.Name, target.Environment, target.Group,
                    previousStatus = previous, status = state.Status,
                    checkedAt = state.CheckedAt, detail = state.Detail
                }, timeout.Token);
            if (!response.IsSuccessStatusCode)
                logger.LogWarning("Monitoring alert delivery failed (HTTP {Status}).", (int)response.StatusCode);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            logger.LogWarning("Monitoring alert delivery failed ({Type}).", exception.GetType().Name);
        }
    }
}
