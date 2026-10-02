using MonitorDashboard.Services;
using ViewerLogLevel = Serilog.Viewer.Models.LogLevel;
using Xunit;

public sealed class LogParserTests
{
    [Theory]
    [InlineData(200, ViewerLogLevel.Information)]
    [InlineData(404, ViewerLogLevel.Warning)]
    [InlineData(502, ViewerLogLevel.Error)]
    public void NginxAccessLogPreservesTimestampOffsetAndRequest(int status, ViewerLogLevel level)
    {
        const string requestId = "93e74558-5b25-4271-a33d-24754de31772";
        var line = $"127.0.0.1 - {requestId} [01/Oct/2026:19:58:18 +0530] \"GET /api/example?value=test HTTP/1.1\" {status} 75 \"-\" \"PostmanRuntime/7.49.1\"";
        var parser = new NginxLogParser();
        Assert.True(parser.CanParse(line));
        var entry = parser.Parse(line, "access.log", 42);
        Assert.NotNull(entry);
        Assert.Equal(new DateTimeOffset(2026, 10, 1, 19, 58, 18, TimeSpan.FromMinutes(330)), entry.Timestamp);
        Assert.Equal(level, entry.Level);
        Assert.Equal(requestId, entry.RequestId);
        Assert.Equal(75L, entry.Properties["BytesSent"]);
        Assert.Contains("GET /api/example?value=test HTTP/1.1", entry.Message);
        Assert.Equal("access.log:42", entry.Id);
    }

    [Fact]
    public void CommonAccessLogSupportsUnknownResponseSize()
    {
        var entry = new NginxLogParser().Parse("::1 - - [01/Oct/2026:19:58:18 +0000] \"GET / HTTP/1.1\" 200 -", "access.log", 0);
        Assert.NotNull(entry);
        Assert.Null(entry.Properties["BytesSent"]);
        Assert.Equal(TimeSpan.Zero, entry.Timestamp.Offset);
    }

    [Fact]
    public void NginxErrorLogPreservesErrorSeverity()
    {
        var entry = new NginxLogParser().Parse("2026/10/01 19:58:18 [error] 123#123: *1 upstream connection failed", "error.log", 0);
        Assert.NotNull(entry);
        Assert.Equal(ViewerLogLevel.Error, entry.Level);
        Assert.Equal("Nginx.Error", entry.SourceContext);
        Assert.Contains("upstream connection failed", entry.Message);
    }

    [Fact]
    public void InvalidTimestampIsNotInvented()
    {
        var parser = new NginxLogParser();
        Assert.Null(parser.Parse("127.0.0.1 - - [31/Feb/2026:19:58:18 +0530] \"GET / HTTP/1.1\" 200 10", "access.log", 0));
        Assert.Null(parser.Parse("unrecognized line", "access.log", 0));
    }

    [Fact]
    public void DashboardLogDatesAreParsedDayFirst()
    {
        var entry = new DashboardLogParser().Parse("04/03/2026 05:06:07 +05:30 [ERR] Request failed", "app.log", 0);
        Assert.NotNull(entry);
        Assert.Equal(4, entry.Timestamp.Day);
        Assert.Equal(3, entry.Timestamp.Month);
        Assert.Equal(ViewerLogLevel.Error, entry.Level);
        Assert.Equal("Request failed", entry.Message);
    }
}
