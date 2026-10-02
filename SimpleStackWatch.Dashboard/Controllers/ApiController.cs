using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MonitorDashboard.Services;
namespace MonitorDashboard.Controllers;
[Authorize, Route("api")]
public sealed class ApiController(ProcessService processes, PostgresService postgres, RedisService redis, AwsService aws, MonitorService monitor, HealthCheckService health, ILogger<ApiController> logger) : ControllerBase
{
    [HttpGet("processes")]
    public Task<IActionResult> Processes(CancellationToken ct) => Read(() => processes.ListAsync(ct));
    [HttpPost("processes/{pid:int}/counters")]
    public Task<IActionResult> Counters(int pid, CancellationToken ct) => Read(async () => new { csv = await processes.CountersAsync(pid, ct) });
    [HttpGet("postgres/databases")]
    public IActionResult PostgresDatabases() => Ok(postgres.Databases());
    [HttpGet("postgres")]
    public Task<IActionResult> Postgres(CancellationToken ct, [FromQuery] int database = 0)
    {
        if (!postgres.HasDatabase(database))
            return Task.FromResult<IActionResult>(BadRequest(new { detail = "Select a configured PostgreSQL database." }));
        return Read(() => postgres.SessionsAsync(database, ct));
    }
    [HttpGet("redis")]
    public Task<IActionResult> Redis(CancellationToken ct) => Read(() => redis.StatsAsync(ct));
    [HttpGet("ec2")]
    public Task<IActionResult> Ec2(CancellationToken ct) => Read(() => aws.InstancesAsync(ct));
    [HttpGet("health/{service}")]
    public Task<IActionResult> Health(string service, CancellationToken ct, [FromQuery] int? database = null) => Read(async () =>
    {
        if (service is not ("postgres" or "redis")) throw new InvalidOperationException("Unknown service.");
        if (service == "postgres" && database.HasValue && !postgres.HasDatabase(database.Value))
            return new { configured = false, status = "Unconfigured", durationMs = 0d };
        var tag = service == "postgres" && database.HasValue ? $"postgres:{database.Value}" : service;
        var report = await health.CheckHealthAsync(r => r.Tags.Contains(tag), ct);
        return new { configured = report.Entries.Count > 0, status = report.Status.ToString(), durationMs = report.TotalDuration.TotalMilliseconds };
    });
    [HttpGet("monitor/processes")]
    public Task<IActionResult> MonitorProcesses(CancellationToken ct) => Read(async () => new { text = await monitor.ReadAsync("processes", null, ct) });
    [HttpGet("monitor/metrics")]
    public Task<IActionResult> MonitorMetrics(CancellationToken ct) => Read(async () => new { text = await monitor.ReadAsync("metrics", null, ct) });
    private async Task<IActionResult> Read<T>(Func<Task<T>> action)
    {
        try { return Ok(await action()); }
        catch (Exception ex) when (ex is not OperationCanceledException || !HttpContext.RequestAborted.IsCancellationRequested)
        {
            // Do not log exception messages: database/Redis exceptions can contain connection secrets.
            logger.LogWarning("Monitoring request failed ({ErrorType}).", ex.GetType().Name);
            return Problem(statusCode: 503, title: "Monitoring data unavailable", detail: "Check the configured connection, permissions, and diagnostic tools on the server.");
        }
    }
}
