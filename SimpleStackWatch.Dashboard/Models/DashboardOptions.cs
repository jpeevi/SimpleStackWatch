namespace MonitorDashboard.Models;
public sealed class DashboardOptions
{
    public string DataDirectory { get; set; } = "data";
    public int RefreshSeconds { get; set; } = 10;
    public int LoginRequestsPerMinute { get; set; } = 20;
    public LogOptions Logs { get; set; } = new();
    public ProcessOptions Processes { get; set; } = new();
    public PostgresOptions Postgres { get; set; } = new();
    public RedisOptions Redis { get; set; } = new();
    public AwsOptions Aws { get; set; } = new();
    public MonitoringOptions Monitoring { get; set; } = new();
    public McpOptions Mcp { get; set; } = new();
}
public sealed class LogOptions
{
    public string Folder { get; set; } = "logs";
    public bool EnableFileViewer { get; set; } = true;
    public List<LogFolderOptions> Folders { get; set; } = [];
    public DatabaseLogOptions Database { get; set; } = new();
}
public sealed class LogFolderOptions
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Folder { get; set; } = "";
    public string BasePath => "/integrations/file-logs/" + Id;
    public string Url => BasePath + "/";
}
public sealed class DatabaseLogOptions
{
    public bool Enabled { get; set; }
    public string ConnectionString { get; set; } = "";
    public string Schema { get; set; } = "public";
    public string Table { get; set; } = "logs";
}
public sealed class ProcessOptions
{
    public string CountersExecutable { get; set; } = "dotnet-counters";
    public int[] AllowedProcessIds { get; set; } = [];
    public int CounterSampleSeconds { get; set; } = 5;
    public int CommandTimeoutSeconds { get; set; } = 20;
    public int MaximumConcurrentSamples { get; set; } = 2;
    public MonitorOptions Monitor { get; set; } = new();
}
public sealed class MonitorOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = "http://127.0.0.1:52323";
    public string MetricsBaseUrl { get; set; } = "http://127.0.0.1:52325";
    public string ApiKey { get; set; } = "";
}
public sealed class PostgresOptions
{
    public List<PostgresDatabaseOptions> Databases { get; set; } = [];
    public string Environment { get; set; } = "Default";
    public string Group { get; set; } = "Databases";
    public bool ShowQueryText { get; set; }
    public int MaximumRows { get; set; } = 100;
}
public sealed class PostgresDatabaseOptions
{
    public string Name { get; set; } = "";
    // Set at startup from environment variables only, never from JSON.
    public string ConnectionString { get; internal set; } = "";
}
public sealed class RedisOptions
{
    public string ConnectionString { get; set; } = "";
    public string Environment { get; set; } = "Default";
    public string Group { get; set; } = "Cache";
}
public sealed class AwsOptions
{
    public bool Enabled { get; set; }
    public string Region { get; set; } = "";
    public string[] InstanceIds { get; set; } = [];
    public string Environment { get; set; } = "Default";
    public string Group { get; set; } = "Infrastructure";
    public int MetricWindowMinutes { get; set; } = 15;
    public int MetricPeriodSeconds { get; set; } = 300;
}
public sealed record DashboardViewModel(string Tab, int RefreshSeconds, bool FileLogs, bool DatabaseLogs, bool MonitorEnabled, IReadOnlyList<LogFolderOptions> LogFolders);

// All monitoring targets are operator-configured; none are application-specific.
public sealed class MonitoringOptions
{
    public bool Enabled { get; set; } = true;
    public int PollSeconds { get; set; } = 30;
    public int FailureThreshold { get; set; } = 3;
    public int RecoveryThreshold { get; set; } = 2;
    public int RetentionDays { get; set; } = 14;
    public int MaxConcurrentChecks { get; set; } = 6;
    public int MaxHttpEndpoints { get; set; } = 100;
    public List<HttpTargetOptions> Endpoints { get; set; } = [];
    public LinuxMonitoringOptions Linux { get; set; } = new();
    public MonitoringAlertOptions Alerts { get; set; } = new();
}

public sealed class HttpTargetOptions
{
    // Optional: a deterministic ID is generated when omitted.
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Environment { get; set; } = "Default";
    public string Group { get; set; } = "Applications";
    public string Url { get; set; } = "";
    public int ExpectedStatusCode { get; set; } = 200;
    // For endpoint availability: any HTTP response below 500 confirms reachability.
    // Default false retains exact-status health checks for existing applications.
    public bool ReachabilityOnly { get; set; }
    public int TimeoutSeconds { get; set; } = 10;
    public string HeaderName { get; set; } = "";
    // Preserve the original environment index even if the bound list has gaps.
    internal string? ConfigurationIndex { get; set; }
    // Auth header values are read only from environment variables.
    public string HeaderValue { get; internal set; } = "";
}

public sealed class LinuxMonitoringOptions
{
    public bool Enabled { get; set; }
    public string Environment { get; set; } = "Default";
    public string Group { get; set; } = "Host";
    public List<string> DiskPaths { get; set; } = ["/"];
    public List<string> Services { get; set; } = [];
    public int WarningPercent { get; set; } = 80;
    public int CriticalPercent { get; set; } = 95;
}

public sealed class MonitoringAlertOptions
{
    // Loaded exclusively from Dashboard__Monitoring__Alerts__WebhookUrl.
    public string WebhookUrl { get; internal set; } = "";
}

// Disabled until explicitly enabled; the bearer secret is never bound from JSON.
public sealed class McpOptions
{
    public bool Enabled { get; set; }
}
