using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MonitorDashboard.Mcp;
using Xunit;

public sealed class McpTests : IDisposable
{
    private readonly string dataPath = Path.Combine(Path.GetTempPath(), "ssw-mcp-test-" + Guid.NewGuid().ToString("N"));
    private readonly string token = Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N");
    private readonly TestEnvironment testEnvironment = TestEnvironment.Load();
    private readonly WebApplicationFactory<Program> factory;

    public McpTests()
    {
        testEnvironment.Set("Dashboard__Mcp__Enabled", "true");
        testEnvironment.Set("Dashboard__Mcp__BearerToken", token);
        testEnvironment.Set("Dashboard__DataDirectory", dataPath);
        testEnvironment.Set("Dashboard__Logs__EnableFileViewer", "false");
        factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // The health UI's background poller uses a real TCP listener; TestServer has none.
            foreach (var service in services.Where(d => d.ServiceType == typeof(IHostedService) &&
                d.ImplementationType?.FullName?.StartsWith("HealthChecks.UI", StringComparison.Ordinal) == true).ToArray())
                services.Remove(service);
        }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("not sufficiently long and spaces")]
    public void InvalidConfiguredTokensFailClosed(string? candidate)
    {
        Assert.Throws<InvalidOperationException>(() => McpBearerAuthentication.HashConfiguredToken(candidate));
    }

    [Fact]
    public void BearerTokenComparisonRejectsWrongSchemesAndTokens()
    {
        var expectedHash = McpBearerAuthentication.HashConfiguredToken(token);
        Assert.True(McpBearerAuthentication.Matches("Bearer " + token, expectedHash));
        Assert.True(McpBearerAuthentication.Matches("bearer " + token, expectedHash));
        Assert.False(McpBearerAuthentication.Matches("Bearer " + token + "x", expectedHash));
        Assert.False(McpBearerAuthentication.Matches("Basic " + token, expectedHash));
        Assert.False(McpBearerAuthentication.Matches(null, expectedHash));
    }

    [Fact]
    public async Task McpRequiresItsOwnBearerToken()
    {
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/mcp")).StatusCode);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "initialize" })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token + "bad");
        var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", response.Headers.WwwAuthenticate.Single().Scheme);

        // An MCP token cannot be used to bypass the dashboard's cookie login.
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/overview")).StatusCode);
    }

    [Fact]
    public async Task McpInitializeAndListReadOnlyTools()
    {
        using var client = factory.CreateClient();
        using var init = await SendAsync(client, 1, "initialize", new
        {
            protocolVersion = "2025-06-18",
            capabilities = new { },
            clientInfo = new { name = "stackwatch-test", version = "1.0" }
        });
        Assert.Equal(HttpStatusCode.OK, init.StatusCode);
        Assert.Contains("protocolVersion", await init.Content.ReadAsStringAsync());

        using var tools = await SendAsync(client, 2, "tools/list", new { });
        Assert.Equal(HttpStatusCode.OK, tools.StatusCode);
        var response = await tools.Content.ReadAsStringAsync();
        Assert.Contains("get_overview", response);
        Assert.Contains("list_targets", response);
        Assert.Contains("get_target_status", response);
        Assert.Contains("get_target_history", response);

        using var call = await SendAsync(client, 3, "tools/call", new
        {
            name = "list_targets", arguments = new { }
        });
        Assert.Equal(HttpStatusCode.OK, call.StatusCode);
        Assert.Contains("result", await call.Content.ReadAsStringAsync());

        using var unknown = await SendAsync(client, 4, "tools/call", new
        {
            name = "get_target_status", arguments = new { targetId = "not-configured" }
        });
        Assert.Equal(HttpStatusCode.OK, unknown.StatusCode);
        Assert.Contains("Unknown monitoring target", await unknown.Content.ReadAsStringAsync());
    }

    private async Task<HttpResponseMessage> SendAsync(HttpClient client, int id, string method, object parameters)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id, method, @params = parameters })
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return await client.SendAsync(request);
    }

    public void Dispose()
    {
        factory.Dispose();
        testEnvironment.Dispose();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(dataPath)) Directory.Delete(dataPath, recursive: true);
    }
}
