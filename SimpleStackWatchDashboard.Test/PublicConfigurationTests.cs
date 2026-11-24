using System.Text.Json;
using Xunit;

public sealed class PublicConfigurationTests
{
    [Fact]
    public void CommittedAppsettingsContainOnlyGenericConfiguration()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(
            Path.Combine(directory.FullName, "SimpleStackWatch.Dashboard", "appsettings.json")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        string[] privatePaths =
        [
            "ConnectionStrings", "AllowedHosts", "Urls", "Serilog:WriteTo",
            "Dashboard:Logs:Folders", "Dashboard:Logs:Database:ConnectionString",
            "Dashboard:Processes:AllowedProcessIds", "Dashboard:Processes:Monitor:BaseUrl",
            "Dashboard:Processes:Monitor:MetricsBaseUrl", "Dashboard:Processes:Monitor:ApiKey",
            "Dashboard:Postgres:Databases", "Dashboard:Postgres:Environment", "Dashboard:Postgres:Group",
            "Dashboard:Redis", "Dashboard:Aws:Region", "Dashboard:Aws:InstanceIds",
            "Dashboard:Aws:Environment", "Dashboard:Aws:Group",
            "Dashboard:Monitoring:Endpoints", "Dashboard:Monitoring:Alerts:WebhookUrl",
            "Dashboard:Monitoring:Linux:Environment", "Dashboard:Monitoring:Linux:Group",
            "Dashboard:Monitoring:Linux:DiskPaths", "Dashboard:Monitoring:Linux:Services",
            "Dashboard:Mcp:BearerToken"
        ];

        foreach (var file in new[] { "appsettings.json", "appsettings.Uat.json" })
        {
            using var document = JsonDocument.Parse(File.ReadAllText(
                Path.Combine(directory!.FullName, "SimpleStackWatch.Dashboard", file)));
            var paths = PropertyPaths(document.RootElement, "").ToArray();
            foreach (var forbidden in privatePaths)
                Assert.DoesNotContain(paths, path =>
                    path.Equals(forbidden, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(forbidden + ":", StringComparison.OrdinalIgnoreCase));

            // Generic local defaults are safe; real server paths must come from the environment.
            if (document.RootElement.TryGetProperty("Dashboard", out var dashboard) &&
                dashboard.TryGetProperty("DataDirectory", out var dataDirectory))
                Assert.Equal("data", dataDirectory.GetString());
            if (document.RootElement.TryGetProperty("LogSettings", out var logSettings) &&
                logSettings.TryGetProperty("FilePath", out var logFile))
                Assert.Equal("logs/app-.log", logFile.GetString());
        }
    }

    private static IEnumerable<string> PropertyPaths(JsonElement node, string prefix)
    {
        if (node.ValueKind != JsonValueKind.Object) yield break;
        foreach (var property in node.EnumerateObject())
        {
            var path = prefix.Length == 0 ? property.Name : prefix + ":" + property.Name;
            yield return path;
            foreach (var child in PropertyPaths(property.Value, path)) yield return child;
        }
    }
}
