using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Data.Sqlite;
using MonitorDashboard.Auth;
using Xunit;
[assembly: CollectionBehavior(DisableTestParallelization = true)]

public sealed class DashboardFixture : IDisposable
{
    public readonly string DataPath = Path.Combine(Path.GetTempPath(), "dashboard-tests-" + Guid.NewGuid().ToString("N"));
    public WebApplicationFactory<Program> Factory { get; }
    private readonly string? oldData = Environment.GetEnvironmentVariable("Dashboard__DataDirectory");
    private readonly string? oldLogs = Environment.GetEnvironmentVariable("Dashboard__Logs__EnableFileViewer");
    private readonly string? oldLimit = Environment.GetEnvironmentVariable("Dashboard__LoginRequestsPerMinute");
    public DashboardFixture()
    {
        Environment.SetEnvironmentVariable("Dashboard__DataDirectory", DataPath);
        Environment.SetEnvironmentVariable("Dashboard__Logs__EnableFileViewer", "false");
        Environment.SetEnvironmentVariable("Dashboard__LoginRequestsPerMinute", "200");
        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            // TestServer has no TCP listener for the health UI's internal HTTP poller.
            foreach (var descriptor in services.Where(d => d.ServiceType == typeof(IHostedService)
                && d.ImplementationType?.FullName?.StartsWith("HealthChecks.UI", StringComparison.Ordinal) == true).ToArray())
                services.Remove(descriptor);
        }));
    }
    public HttpClient Client() => Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    public async Task<(string Name, string Key, string Recovery)> UserAsync()
    {
        using var scope = Factory.Services.CreateScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<DashboardUser>>();
        var name = "user" + Guid.NewGuid().ToString("N");
        var user = new DashboardUser { UserName = name, Enabled = true };
        Assert.True((await manager.CreateAsync(user, "correct horse battery staple")).Succeeded);
        Assert.True((await manager.ResetAuthenticatorKeyAsync(user)).Succeeded);
        Assert.True((await manager.SetTwoFactorEnabledAsync(user, true)).Succeeded);
        var key = (await manager.GetAuthenticatorKeyAsync(user))!;
        var codes = (await manager.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.ToArray();
        return (name, key, codes[0]);
    }
    public void Dispose()
    {
        Factory.Dispose();
        Environment.SetEnvironmentVariable("Dashboard__DataDirectory", oldData);
        Environment.SetEnvironmentVariable("Dashboard__Logs__EnableFileViewer", oldLogs);
        Environment.SetEnvironmentVariable("Dashboard__LoginRequestsPerMinute", oldLimit);
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(DataPath)) Directory.Delete(DataPath, true);
    }
}

public sealed class AuthTests(DashboardFixture fixture) : IClassFixture<DashboardFixture>
{
    [Theory]
    [InlineData("/api/processes")]
    [InlineData("/api/postgres")]
    [InlineData("/api/redis")]
    [InlineData("/api/ec2")]
    public async Task AnonymousApisAreProtected(string path)
    {
        using var client = fixture.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(path)).StatusCode);
    }
    [Theory]
    [InlineData("/")]
    [InlineData("/integrations/file-logs/api/files")]
    [InlineData("/integrations/file-logs/nginx/")]
    [InlineData("/integrations/file-logs/my-app/api/files")]
    [InlineData("/integrations/database-logs")]
    [InlineData("/integrations/health")]
    [InlineData("/integrations/health-api")]
    public async Task AnonymousViewerRoutesAreProtected(string path)
    {
        using var client = fixture.Client();
        var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/login", response.Headers.Location?.OriginalString);
    }
    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("::1", true)]
    [InlineData("10.0.0.10", false)]
    public async Task ForwardedHeadersAreAcceptedOnlyFromTheLocalProxy(string peer, bool trusted)
    {
        var options = fixture.Factory.Services
            .GetRequiredService<Microsoft.Extensions.Options.IOptions<Microsoft.AspNetCore.Builder.ForwardedHeadersOptions>>().Value;
        Assert.Equal(1, options.ForwardLimit);
        Assert.Empty(options.KnownIPNetworks);
        Assert.Equal(2, options.KnownProxies.Count);
        using var logs = Microsoft.Extensions.Logging.LoggerFactory.Create(_ => { });
        var middleware = new Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersMiddleware(
            _ => Task.CompletedTask, logs, Microsoft.Extensions.Options.Options.Create(options));
        var context = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        context.Request.Scheme = "http";
        context.Request.Host = new Microsoft.AspNetCore.Http.HostString("stackwatch.lumestack.local");
        context.Connection.RemoteIpAddress = IPAddress.Parse(peer);
        context.Request.Headers["X-Forwarded-For"] = "192.0.2.25";
        context.Request.Headers["X-Forwarded-Proto"] = "https";
        await middleware.Invoke(context);
        Assert.Equal(trusted ? "https" : "http", context.Request.Scheme);
        Assert.Equal(IPAddress.Parse(trusted ? "192.0.2.25" : peer), context.Connection.RemoteIpAddress);
        Assert.Equal("stackwatch.lumestack.local", context.Request.Host.Value);
    }

    [Fact]
    public async Task HealthUiUsesAbsoluteRoutesAndServesItsAssets()
    {
        var user = await fixture.UserAsync();
        using var client = fixture.Client();
        await PasswordAsync(client, user.Name);
        Assert.Equal(HttpStatusCode.Redirect, (await VerifyAsync(client, Totp(user.Key))).StatusCode);
        var page = await client.GetAsync("/integrations/health");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("src=\"/integrations/health-resources/vendors-dll.js\"", html);
        Assert.Contains("src=\"/integrations/health-resources/healthchecks-bundle.js\"", html);
        Assert.Contains("href=\"/integrations/health-resources/healthchecksui-min.css\"", html);
        Assert.Contains("var uiEndpoint = \"/integrations/health-api\"", html);
        Assert.Contains("var webhookEndpoint = \"/integrations/health-webhooks\"", html);
        Assert.DoesNotContain("/integrations/integrations/", html);
        foreach (var file in new[] { "vendors-dll.js", "healthchecks-bundle.js", "healthchecksui-min.css" })
        {
            var asset = await client.GetAsync($"/integrations/health-resources/{file}");
            Assert.Equal(HttpStatusCode.OK, asset.StatusCode);
            Assert.NotEmpty(await asset.Content.ReadAsByteArrayAsync());
            var mime = asset.Content.Headers.ContentType?.MediaType;
            if (file.EndsWith(".css")) Assert.Equal("text/css", mime);
            else Assert.True(mime is "text/javascript" or "application/javascript");
            if (file == "healthchecks-bundle.js")
                Assert.Contains("DD/MM/YYYY HH:mm:ss", await asset.Content.ReadAsStringAsync());
        }
    }
    [Fact]
    public async Task HealthzRequiresPrivateCredential()
    {
        using var client = fixture.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/healthz")).StatusCode);
    }
    [Fact]
    public async Task PasswordAloneCannotAccessDashboard()
    {
        var user = await fixture.UserAsync();
        using var client = fixture.Client();
        Assert.Equal(HttpStatusCode.Redirect, (await PasswordAsync(client, user.Name)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/redis")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/account/verify")).StatusCode);
    }
    [Fact]
    public async Task ValidTotpSignsInAndStampResetRevokesSession()
    {
        var user = await fixture.UserAsync();
        using var client = fixture.Client();
        await PasswordAsync(client, user.Name);
        Assert.Equal(HttpStatusCode.Redirect, (await VerifyAsync(client, Totp(user.Key))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/dashboard/logs")).StatusCode);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<DashboardUser>>();
            Assert.True((await users.UpdateSecurityStampAsync((await users.FindByNameAsync(user.Name))!)).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/redis")).StatusCode);
    }
    [Fact]
    public async Task RecoveryCodeIsSingleUse()
    {
        var user = await fixture.UserAsync();
        using var first = fixture.Client();
        await PasswordAsync(first, user.Name);
        Assert.Equal(HttpStatusCode.Redirect, (await VerifyAsync(first, user.Recovery, true)).StatusCode);
        using var second = fixture.Client();
        await PasswordAsync(second, user.Name);
        Assert.Equal(HttpStatusCode.OK, (await VerifyAsync(second, user.Recovery, true)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await second.GetAsync("/api/redis")).StatusCode);
    }
    [Fact]
    public async Task DisableRevokesExistingSession()
    {
        var user = await fixture.UserAsync();
        using var client = fixture.Client();
        await PasswordAsync(client, user.Name);
        await VerifyAsync(client, Totp(user.Key));
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<DashboardUser>>();
            var account = (await users.FindByNameAsync(user.Name))!;
            account.Enabled = false;
            Assert.True((await users.UpdateAsync(account)).Succeeded);
        }
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/redis")).StatusCode);
    }
    [Fact]
    public async Task ResetInvalidatesPendingMfaChallenge()
    {
        var user = await fixture.UserAsync();
        using var client = fixture.Client();
        await PasswordAsync(client, user.Name);
        using (var scope = fixture.Factory.Services.CreateScope())
        {
            var users = scope.ServiceProvider.GetRequiredService<UserManager<DashboardUser>>();
            Assert.True((await users.UpdateSecurityStampAsync((await users.FindByNameAsync(user.Name))!)).Succeeded);
        }
        var response = await VerifyAsync(client, Totp(user.Key));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/account/login", response.Headers.Location?.OriginalString);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/redis")).StatusCode);
    }
    [Fact]
    public async Task AuthenticatorKeyIsEncryptedAtRest()
    {
        var user = await fixture.UserAsync();
        await using var connection = new SqliteConnection($"Data Source={Path.Combine(fixture.DataPath, "users.db")}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT t.Value FROM AspNetUserTokens t JOIN AspNetUsers u ON u.Id=t.UserId WHERE u.UserName=@name AND t.Name='AuthenticatorKey'";
        command.Parameters.AddWithValue("@name", user.Name);
        var stored = (string)(await command.ExecuteScalarAsync())!;
        Assert.NotEqual(user.Key, stored);
        Assert.DoesNotContain(user.Key, stored);
    }
    [Fact]
    public async Task LoginRequiresAntiforgeryToken()
    {
        using var client = fixture.Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = "test", ["password"] = "test" }))).StatusCode);
    }
    private static async Task<HttpResponseMessage> PasswordAsync(HttpClient client, string name)
    {
        var token = await TokenAsync(client, "/account/login");
        return await client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string> { ["username"] = name, ["password"] = "correct horse battery staple", ["__RequestVerificationToken"] = token }));
    }
    private static async Task<HttpResponseMessage> VerifyAsync(HttpClient client, string code, bool recovery = false)
    {
        var token = await TokenAsync(client, "/account/verify");
        return await client.PostAsync("/account/verify", new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = code, ["recovery"] = recovery.ToString(), ["__RequestVerificationToken"] = token }));
    }
    private static async Task<string> TokenAsync(HttpClient client, string path)
    {
        var html = await client.GetStringAsync(path);
        var match = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(match.Success);
        return WebUtility.HtmlDecode(match.Groups[1].Value);
    }
    private static string Totp(string base32)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new List<byte>(); int buffer = 0, bits = 0;
        foreach (var c in base32.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c); bits += 5;
            if (bits >= 8) { bits -= 8; bytes.Add((byte)(buffer >> bits)); }
        }
        var step = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30;
        var counter = BitConverter.GetBytes(step);
        if (BitConverter.IsLittleEndian) Array.Reverse(counter);
        var hash = HMACSHA1.HashData(bytes.ToArray(), counter);
        var offset = hash[^1] & 15;
        var binary = ((hash[offset] & 127) << 24) | (hash[offset+1] << 16) | (hash[offset+2] << 8) | hash[offset+3];
        return (binary % 1000000).ToString("D6");
    }
}
