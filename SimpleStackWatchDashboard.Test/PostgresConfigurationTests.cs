using Microsoft.Extensions.Configuration;
using MonitorDashboard.Models;
using MonitorDashboard.Services;
using System.Text.Json;
using Xunit;

public sealed class PostgresConfigurationTests
{
    [Fact]
    public void JsonSecretsAreIgnoredAndCatalogDoesNotExposeEnvironmentSecrets()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Databases:0:Name"] = "Application",
            ["Databases:0:ConnectionString"] = "Password=json-secret",
            ["Databases:1:Name"] = "Reporting"
        }).Build();
        var options = new DashboardOptions { Postgres = configuration.Get<PostgresOptions>()! };
        PostgresService.Configure(options.Postgres, key => key == "Dashboard__Postgres__Databases__0__ConnectionString"
            ? "Host=localhost;Database=app;Password=environment-secret" : null);
        Assert.Contains("environment-secret", options.Postgres.Databases[0].ConnectionString);
        Assert.Equal("", options.Postgres.Databases[1].ConnectionString);
        var catalog = JsonSerializer.Serialize(new PostgresService(options).Databases());
        Assert.DoesNotContain("secret", catalog);
        Assert.DoesNotContain("ConnectionString", catalog);
        Assert.Contains("Application", catalog);
    }

    [Fact]
    public async Task MissingConnectionDoesNotFallBackToAnotherDatabase()
    {
        var options = new DashboardOptions();
        options.Postgres.Databases = [new() { Name = "Application" }, new() { Name = "Reporting" }];
        PostgresService.Configure(options.Postgres, key => key.EndsWith("__0__ConnectionString") ? "Host=localhost;Database=app" : null);
        var service = new PostgresService(options);
        var result = await service.SessionsAsync(1, CancellationToken.None);
        Assert.Contains("\"configured\":false", JsonSerializer.Serialize(result));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => service.SessionsAsync(2, CancellationToken.None));
        Assert.False(service.HasDatabase(-1));
    }

    [Theory]
    [InlineData("Application", "application")]
    [InlineData("Application", " ")]
    public void InvalidNamesFailAtStartup(string first, string second)
    {
        var options = new PostgresOptions { Databases = [new() { Name = first }, new() { Name = second }] };
        Assert.Throws<InvalidOperationException>(() => PostgresService.Configure(options, _ => null));
    }

    [Fact]
    public void PreviousSingleConnectionEnvironmentVariableStillWorksWithoutDatabaseList()
    {
        var options = new PostgresOptions();
        PostgresService.Configure(options, key => key == "Dashboard__Postgres__ConnectionString" ? "Host=localhost;Database=legacy" : null);
        Assert.Single(options.Databases);
        Assert.Equal("Default", options.Databases[0].Name);
    }
}
