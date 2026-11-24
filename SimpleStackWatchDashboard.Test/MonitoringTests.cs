using System.Net;
using Microsoft.Extensions.Configuration;
using MonitorDashboard.Models;
using MonitorDashboard.Services;
using Xunit;

public sealed class MonitoringTests
{
    [Fact]
    public void FailedChecksAreDebouncedAndRecoveryRequiresConsecutiveSuccesses()
    {
        var options = new MonitoringOptions { FailureThreshold = 3, RecoveryThreshold = 2 };
        MonitoringState? state = null;
        var start = DateTimeOffset.UtcNow;
        string Step(string value)
        {
            state = MonitoringStatus.Advance("api", state, new MonitoringReading(value, 12, "check"), start, options);
            return state.Status;
        }
        Assert.Equal("Degraded", Step("Down"));
        Assert.Equal("Degraded", Step("Down"));
        Assert.Equal("Down", Step("Down"));
        Assert.Equal("Degraded", Step("Healthy"));
        Assert.Equal("Healthy", Step("Healthy"));
        Assert.Equal("Degraded", Step("Degraded"));
        Assert.Equal("Unknown", Step("Unknown"));
        Assert.Equal("Healthy", Step("Healthy"));
    }

    [Fact]
    public void TargetsAreGenericAndInvalidDuplicatesAndUrlsAreRejected()
    {
        var settings = new DashboardOptions();
        settings.Monitoring.Endpoints.Add(new HttpTargetOptions
        {
            Id = "api", Name = "Example API", Environment = "staging", Url = "https://example.org/health"
        });
        Assert.Single(MonitoringService.BuildTargets(settings));
        settings.Monitoring.Endpoints.Add(new HttpTargetOptions
        {
            Id = "API", Name = "Duplicate", Url = "https://example.net/health"
        });
        Assert.Throws<InvalidOperationException>(() => MonitoringService.BuildTargets(settings));
        settings.Monitoring.Endpoints.RemoveAt(1);
        settings.Monitoring.Endpoints[0].Url = "file:///etc/passwd";
        Assert.Throws<InvalidOperationException>(() => MonitoringService.BuildTargets(settings));
    }

    [Fact]
    public void NameAndUrlAreSufficientAndGeneratedIdsAreStableAcrossReordering()
    {
        static HttpTargetOptions First() => new()
        {
            Name = "Example API", Url = "https://example.org/health"
        };
        static HttpTargetOptions Second() => new()
        {
            Name = "Example API", Url = "https://example.net/health"
        };
        var options = new DashboardOptions();
        options.Monitoring.Endpoints = [First(), Second()];
        var original = MonitoringService.BuildTargets(options);
        Assert.Equal(2, original.Count);
        Assert.NotEqual(original[0].Id, original[1].Id);
        Assert.Matches("^http-[a-f0-9]{64}$", original[0].Id);
        Assert.Equal(original[0].Id, options.Monitoring.Endpoints[0].Id);
        Assert.Equal("Applications", original[0].Group);
        Assert.Equal("Default", original[0].Environment);
        Assert.Equal(original[0].Id, MonitoringService.BuildTargets(options)[0].Id);

        options.Monitoring.Endpoints = [Second(), First()];
        var reordered = MonitoringService.BuildTargets(options);
        Assert.Equal(original[0].Id, reordered[1].Id);
        Assert.Equal(original[1].Id, reordered[0].Id);

        options.Monitoring.Endpoints = [First(), First()];
        Assert.Throws<InvalidOperationException>(() => MonitoringService.BuildTargets(options));
    }

    [Fact]
    public void ExplicitIdsAndEnvironmentSpecificAutomaticIdsRemainDistinct()
    {
        var options = new DashboardOptions();
        options.Monitoring.Endpoints =
        [
            new() { Name = "API", Url = "https://example.org/health", Environment = "Test" },
            new() { Name = "API", Url = "https://example.org/health", Environment = "Uat" },
            new() { Id = "existing-api", Name = "API", Url = "https://example.org/health" }
        ];
        var targets = MonitoringService.BuildTargets(options);
        Assert.Equal(3, targets.Select(t => t.Id).Distinct().Count());
        Assert.Equal("existing-api", targets[2].Id);
    }

    [Theory]
    [InlineData("", "https://example.org/health")]
    [InlineData("API", "")]
    [InlineData("API", "file:///etc/passwd")]
    public void AutomaticIdsDoNotBypassEndpointValidation(string name, string url)
    {
        var options = new DashboardOptions();
        options.Monitoring.Endpoints.Add(new() { Name = name, Url = url });
        Assert.Throws<InvalidOperationException>(() => MonitoringService.BuildTargets(options));
    }

    [Fact]
    public void ConfigurableEndpointLimitAllowsMoreThanOneHundred()
    {
        var options = new DashboardOptions();
        options.Monitoring.Endpoints = Enumerable.Range(0, 101).Select(i => new HttpTargetOptions
        {
            Name = $"API {i}", Url = $"https://example.org/health/{i}"
        }).ToList();
        Assert.Throws<InvalidOperationException>(() => MonitoringService.BuildTargets(options));
        options.Monitoring.MaxHttpEndpoints = 101;
        Assert.Equal(101, MonitoringService.BuildTargets(options).Count);
        options.Monitoring.MaxHttpEndpoints = 0;
        Assert.Throws<InvalidOperationException>(() => MonitoringService.BuildTargets(options));
    }

    [Fact]
    public void S3AvailabilityTargetUsesSharedOverviewAndMcpMonitoringList()
    {
        var options = new DashboardOptions();
        options.Monitoring.Endpoints.Add(new HttpTargetOptions
        {
            Id = "object-storage", Name = "Object storage", Environment = "Uat",
            Group = "Infrastructure", Url = "https://s3.us-east-1.amazonaws.com",
            ReachabilityOnly = true
        });

        var target = Assert.Single(MonitoringService.BuildTargets(options));
        Assert.Equal("object-storage", target.Id);
        Assert.Equal("http", target.Kind);
        Assert.Equal("Uat", target.Environment);
        Assert.Equal("Infrastructure", target.Group);
    }

    [Theory]
    [InlineData(200, true)]
    [InlineData(301, true)]
    [InlineData(403, true)]
    [InlineData(404, true)]
    [InlineData(429, true)]
    [InlineData(500, false)]
    [InlineData(503, false)]
    public void ReachabilityCheckTreatsClientErrorsAsReachableAndServerErrorsAsDown(
        int responseCode, bool expectedHealthy)
    {
        var endpoint = new HttpTargetOptions { ReachabilityOnly = true };
        Assert.Equal(expectedHealthy, MonitoringService.IsHttpStatusHealthy(
            endpoint, (HttpStatusCode)responseCode));
    }

    [Fact]
    public void ExistingHttpChecksStillRequireExpectedStatusCode()
    {
        var endpoint = new HttpTargetOptions();
        Assert.False(endpoint.ReachabilityOnly);
        Assert.True(MonitoringService.IsHttpStatusHealthy(endpoint, HttpStatusCode.OK));
        Assert.False(MonitoringService.IsHttpStatusHealthy(endpoint, HttpStatusCode.Forbidden));
        endpoint.ExpectedStatusCode = 204;
        Assert.True(MonitoringService.IsHttpStatusHealthy(endpoint, HttpStatusCode.NoContent));
        Assert.False(MonitoringService.IsHttpStatusHealthy(endpoint, HttpStatusCode.OK));
    }

    [Fact]
    public void EndpointAuthenticationHeaderOnlyLoadsFromEnvironment()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Endpoints:0:Id"] = "endpoint",
            ["Endpoints:0:Name"] = "Example",
            ["Endpoints:0:Url"] = "https://example.org/health",
            ["Endpoints:0:HeaderName"] = "Authorization",
            ["Endpoints:0:HeaderValue"] = "json-token",
            ["Alerts:WebhookUrl"] = "https://json-secret.example.org"
        }).Build();
        var options = config.Get<MonitoringOptions>()!;
        Assert.Empty(options.Endpoints[0].HeaderValue);
        Assert.Empty(options.Alerts.WebhookUrl);
        MonitoringService.Configure(options, key => key switch
        {
            "Dashboard__Monitoring__Endpoints__0__HeaderValue" => "Bearer secret",
            "Dashboard__Monitoring__Alerts__WebhookUrl" => "https://example.org/alert",
            _ => null
        });
        Assert.Equal("Bearer secret", options.Endpoints[0].HeaderValue);
        Assert.Equal("https://example.org/alert", options.Alerts.WebhookUrl);
    }

    [Fact]
    public async Task HistoryPersistsAndCanBeReadBack()
    {
        var directory = Path.Combine(Path.GetTempPath(), "ssw-monitor-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var store = new MonitoringStore(new DashboardOptions { DataDirectory = directory });
            await store.InitializeAsync();
            var now = DateTimeOffset.UtcNow;
            var state = new MonitoringState("api", "Down", 3, 0, now, 42, "HTTP 503.");
            await store.SaveAsync([state], [new MonitoringSample("api", "Down", now, 42, "HTTP 503.")],
                now.AddDays(-14), CancellationToken.None);
            var states = await store.ReadStatesAsync(CancellationToken.None);
            Assert.Equal("Down", states["api"].Status);
            var rows = await store.ReadHistoryAsync("api", 50, CancellationToken.None);
            Assert.Single(rows);
            Assert.Equal(42d, rows[0].DurationMs!.Value);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
