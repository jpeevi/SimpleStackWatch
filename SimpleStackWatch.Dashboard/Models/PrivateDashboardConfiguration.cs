using Microsoft.Extensions.Configuration;

namespace MonitorDashboard.Models;

// Environment-specific hosts, identifiers, paths and credentials must not be
// accepted from committed appsettings JSON or command-line configuration.
// Pass an IConfiguration built exclusively from environment variables.
public static class PrivateDashboardConfiguration
{
    public static void Apply(DashboardOptions options, IConfiguration environment)
    {
        var settings = environment.GetSection("Dashboard");

        options.DataDirectory = settings["DataDirectory"] ?? "data";
        options.Logs.Folder = settings["Logs:Folder"] ?? "logs";
        options.Logs.Folders = settings.GetSection("Logs:Folders").Get<List<LogFolderOptions>>() ?? [];
        options.Logs.Database.ConnectionString = settings["Logs:Database:ConnectionString"] ?? "";
        options.Logs.Database.Schema = settings["Logs:Database:Schema"] ?? "public";
        options.Logs.Database.Table = settings["Logs:Database:Table"] ?? "logs";

        options.Processes.CountersExecutable = settings["Processes:CountersExecutable"] ?? "dotnet-counters";
        options.Processes.AllowedProcessIds = settings.GetSection("Processes:AllowedProcessIds").Get<int[]>() ?? [];
        options.Processes.Monitor.BaseUrl = settings["Processes:Monitor:BaseUrl"] ?? "http://127.0.0.1:52323";
        options.Processes.Monitor.MetricsBaseUrl = settings["Processes:Monitor:MetricsBaseUrl"] ?? "http://127.0.0.1:52325";
        options.Processes.Monitor.ApiKey = settings["Processes:Monitor:ApiKey"] ?? "";

        options.Postgres.Databases = settings.GetSection("Postgres:Databases")
            .Get<List<PostgresDatabaseOptions>>() ?? [];
        options.Postgres.Environment = settings["Postgres:Environment"] ?? "Default";
        options.Postgres.Group = settings["Postgres:Group"] ?? "Databases";

        options.Redis.ConnectionString = settings["Redis:ConnectionString"] ?? "";
        options.Redis.Environment = settings["Redis:Environment"] ?? "Default";
        options.Redis.Group = settings["Redis:Group"] ?? "Cache";

        options.Aws.Region = settings["Aws:Region"] ?? "";
        options.Aws.InstanceIds = settings.GetSection("Aws:InstanceIds").Get<string[]>() ?? [];
        options.Aws.Environment = settings["Aws:Environment"] ?? "Default";
        options.Aws.Group = settings["Aws:Group"] ?? "Infrastructure";
        if (options.Aws.Enabled && string.IsNullOrWhiteSpace(options.Aws.Region))
            throw new InvalidOperationException(
                "AWS monitoring is enabled but Dashboard__Aws__Region is not configured in the environment.");

        options.Monitoring.Endpoints = settings.GetSection("Monitoring:Endpoints").GetChildren()
            .Select(section =>
            {
                var endpoint = section.Get<HttpTargetOptions>() ?? new();
                endpoint.ConfigurationIndex = section.Key;
                return endpoint;
            }).ToList();
        options.Monitoring.Linux.Environment = settings["Monitoring:Linux:Environment"] ?? "Default";
        options.Monitoring.Linux.Group = settings["Monitoring:Linux:Group"] ?? "Host";
        options.Monitoring.Linux.DiskPaths = settings.GetSection("Monitoring:Linux:DiskPaths")
            .Get<List<string>>() ?? ["/"];
        options.Monitoring.Linux.Services = settings.GetSection("Monitoring:Linux:Services")
            .Get<List<string>>() ?? [];

        // Headers, PostgreSQL connection strings, alert URLs and MCP tokens use
        // their existing environment-only readers after this configuration step.
    }
}
