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
    public bool ShowQueryText { get; set; }
    public int MaximumRows { get; set; } = 100;
}
public sealed class PostgresDatabaseOptions
{
    public string Name { get; set; } = "";
    // Set at startup from environment variables only, never from JSON.
    public string ConnectionString { get; internal set; } = "";
}
public sealed class RedisOptions { public string ConnectionString { get; set; } = ""; }
public sealed class AwsOptions
{
    public bool Enabled { get; set; }
    public string Region { get; set; } = "ap-south-1";
    public string[] InstanceIds { get; set; } = [];
    public int MetricWindowMinutes { get; set; } = 15;
    public int MetricPeriodSeconds { get; set; } = 300;
}
public sealed record DashboardViewModel(string Tab, int RefreshSeconds, bool FileLogs, bool DatabaseLogs, bool MonitorEnabled, IReadOnlyList<LogFolderOptions> LogFolders);
