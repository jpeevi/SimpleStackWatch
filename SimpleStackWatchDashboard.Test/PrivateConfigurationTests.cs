using Microsoft.Extensions.Configuration;
using MonitorDashboard.Models;
using MonitorDashboard.Services;
using Xunit;

public sealed class PrivateConfigurationTests
{
    [Fact]
    public void PrivateInfrastructureValuesFromJsonAreDiscarded()
    {
        var publicConfiguration = Config(new Dictionary<string, string?>
        {
            ["Dashboard:DataDirectory"] = "/private/data",
            ["Dashboard:Logs:Folder"] = "/private/logs",
            ["Dashboard:Logs:Database:ConnectionString"] = "Password=committed-secret",
            ["Dashboard:Logs:Database:Schema"] = "private_schema",
            ["Dashboard:Logs:Folders:0:Id"] = "internal-logs",
            ["Dashboard:Processes:Monitor:ApiKey"] = "committed-key",
            ["Dashboard:Processes:Monitor:BaseUrl"] = "http://private.example.test",
            ["Dashboard:Processes:AllowedProcessIds:0"] = "42",
            ["Dashboard:Postgres:Databases:0:Name"] = "PrivateDatabase",
            ["Dashboard:Redis:ConnectionString"] = "password=committed-secret",
            ["Dashboard:Aws:Region"] = "private-region",
            ["Dashboard:Aws:InstanceIds:0"] = "i-private",
            ["Dashboard:Monitoring:Endpoints:0:Id"] = "private-api",
            ["Dashboard:Monitoring:Endpoints:0:Url"] = "https://private.example.test/health",
            ["Dashboard:Monitoring:Linux:Services:0"] = "internal.service",
            ["Dashboard:Monitoring:Linux:Environment"] = "Internal",
            ["Dashboard:RefreshSeconds"] = "15"
        });
        var options = publicConfiguration.GetSection("Dashboard").Get<DashboardOptions>()!;

        PrivateDashboardConfiguration.Apply(options, Config(new Dictionary<string, string?>()));

        Assert.Equal("data", options.DataDirectory);
        Assert.Equal("logs", options.Logs.Folder);
        Assert.Empty(options.Logs.Folders);
        Assert.Empty(options.Logs.Database.ConnectionString);
        Assert.Equal("public", options.Logs.Database.Schema);
        Assert.Empty(options.Processes.Monitor.ApiKey);
        Assert.Equal("http://127.0.0.1:52323", options.Processes.Monitor.BaseUrl);
        Assert.Empty(options.Processes.AllowedProcessIds);
        Assert.Empty(options.Postgres.Databases);
        Assert.Empty(options.Redis.ConnectionString);
        Assert.Empty(options.Aws.Region);
        Assert.Empty(options.Aws.InstanceIds);
        Assert.Empty(options.Monitoring.Endpoints);
        Assert.Empty(options.Monitoring.Linux.Services);
        Assert.Equal("Default", options.Monitoring.Linux.Environment);
        Assert.Equal(15, options.RefreshSeconds); // Non-sensitive settings retain JSON values.
    }

    [Fact]
    public void OperatorSpecificValuesLoadFromEnvironmentConfiguration()
    {
        var options = new DashboardOptions();
        var environment = Config(new Dictionary<string, string?>
        {
            ["Dashboard:DataDirectory"] = "/var/lib/monitoring",
            ["Dashboard:Logs:Database:ConnectionString"] = "Host=localhost;Password=environment-secret",
            ["Dashboard:Logs:Folders:0:Id"] = "app",
            ["Dashboard:Logs:Folders:0:Name"] = "Application",
            ["Dashboard:Logs:Folders:0:Folder"] = "/var/log/application",
            ["Dashboard:Processes:Monitor:ApiKey"] = "environment-key",
            ["Dashboard:Postgres:Databases:0:Name"] = "Reporting",
            ["Dashboard:Redis:ConnectionString"] = "localhost:6379,password=environment-secret",
            ["Dashboard:Aws:Region"] = "eu-west-1",
            ["Dashboard:Aws:InstanceIds:0"] = "i-example",
            ["Dashboard:Monitoring:Endpoints:0:Id"] = "api",
            ["Dashboard:Monitoring:Endpoints:0:Name"] = "Example API",
            ["Dashboard:Monitoring:Endpoints:0:Url"] = "https://example.org/health",
            ["Dashboard:Monitoring:Endpoints:0:ReachabilityOnly"] = "true",
            ["Dashboard:Monitoring:Linux:Services:0"] = "nginx.service",
            ["Dashboard:Monitoring:Linux:Environment"] = "Staging"
        });

        PrivateDashboardConfiguration.Apply(options, environment);

        Assert.Equal("/var/lib/monitoring", options.DataDirectory);
        Assert.Equal("Host=localhost;Password=environment-secret", options.Logs.Database.ConnectionString);
        Assert.Equal("app", Assert.Single(options.Logs.Folders).Id);
        Assert.Equal("environment-key", options.Processes.Monitor.ApiKey);
        Assert.Equal("Reporting", Assert.Single(options.Postgres.Databases).Name);
        Assert.Contains("environment-secret", options.Redis.ConnectionString);
        Assert.Equal("eu-west-1", options.Aws.Region);
        Assert.Equal("i-example", Assert.Single(options.Aws.InstanceIds));
        var target = Assert.Single(options.Monitoring.Endpoints);
        Assert.Equal("api", target.Id);
        Assert.True(target.ReachabilityOnly);
        Assert.Equal("nginx.service", Assert.Single(options.Monitoring.Linux.Services));
        Assert.Equal("Staging", options.Monitoring.Linux.Environment);
    }

    [Fact]
    public void SparseEndpointIndexesKeepAuthenticationHeadersWithTheirOwnTarget()
    {
        var options = new DashboardOptions();
        var environment = Config(new Dictionary<string, string?>
        {
            ["Dashboard:Monitoring:Endpoints:2:Name"] = "First API",
            ["Dashboard:Monitoring:Endpoints:2:Url"] = "https://example.org/health",
            ["Dashboard:Monitoring:Endpoints:2:HeaderName"] = "Authorization",
            ["Dashboard:Monitoring:Endpoints:5:Name"] = "Second API",
            ["Dashboard:Monitoring:Endpoints:5:Url"] = "https://example.net/health",
            ["Dashboard:Monitoring:Endpoints:5:HeaderName"] = "Authorization"
        });
        PrivateDashboardConfiguration.Apply(options, environment);
        MonitoringService.Configure(options.Monitoring, key => key switch
        {
            "Dashboard__Monitoring__Endpoints__2__HeaderValue" => "Bearer first",
            "Dashboard__Monitoring__Endpoints__5__HeaderValue" => "Bearer second",
            _ => null
        });
        Assert.Collection(options.Monitoring.Endpoints,
            first => { Assert.Equal("First API", first.Name); Assert.Equal("Bearer first", first.HeaderValue); },
            second => { Assert.Equal("Second API", second.Name); Assert.Equal("Bearer second", second.HeaderValue); });
        Assert.Equal(2, MonitoringService.BuildTargets(options).Count);
    }

    [Fact]
    public void EnabledAwsRequiresAnEnvironmentProvidedRegion()
    {
        var options = new DashboardOptions { Aws = new AwsOptions { Enabled = true, Region = "json-region" } };
        Assert.Throws<InvalidOperationException>(() =>
            PrivateDashboardConfiguration.Apply(options, Config(new Dictionary<string, string?>())));
    }

    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
