using System.Globalization;
using System.Text.RegularExpressions;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;
using ViewerLogLevel = Serilog.Viewer.Models.LogLevel;

namespace MonitorDashboard.Services;

public sealed class NginxLogParser : ILogParser
{
    private static readonly Regex Access = new(
        "^(?<client>\\S+)\\s+(?<ident>\\S+)\\s+(?<user>\\S+)\\s+\\[(?<timestamp>[^\\]]+)\\]\\s+\"(?<request>[^\"]*)\"\\s+(?<status>[0-9]{3})\\s+(?<bytes>[0-9]+|-)(?:\\s+\"(?<referer>[^\"]*)\"\\s+\"(?<agent>[^\"]*)\")?(?:\\s+.*)?$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(250));
    private static readonly Regex Error = new(
        "^(?<timestamp>[0-9]{4}/[0-9]{2}/[0-9]{2} [0-9]{2}:[0-9]{2}:[0-9]{2})\\s+\\[(?<level>debug|info|notice|warn|error|crit|alert|emerg)\\]\\s+(?<message>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(250));

    public LogFileFormat Format => LogFileFormat.PlainText;
    public bool CanParse(string line) => !string.IsNullOrWhiteSpace(line) && (Access.IsMatch(line) || Error.IsMatch(line));

    public LogEntry? Parse(string line, string fileName, long lineOffset)
    {
        var match = Access.Match(line);
        if (match.Success)
        {
            var value = match.Groups["timestamp"].Value;
            // Nginx uses +0530; DateTimeOffset's zzz format expects +05:30.
            if (value.Length >= 5 && (value[^5] == '+' || value[^5] == '-'))
                value = value.Insert(value.Length - 2, ":");
            if (!DateTimeOffset.TryParseExact(value, "dd/MMM/yyyy:HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
                return null;

            var status = int.Parse(match.Groups["status"].Value, CultureInfo.InvariantCulture);
            var request = match.Groups["request"].Value;
            var user = match.Groups["user"].Value;
            var requestParts = request.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
            var message = $"{status} {request}";
            return new LogEntry
            {
                Id = $"{fileName}:{lineOffset}", FileName = fileName, LineOffset = lineOffset,
                Timestamp = timestamp,
                Level = status >= 500 ? ViewerLogLevel.Error : status >= 400 ? ViewerLogLevel.Warning : ViewerLogLevel.Information,
                Message = message, RenderedMessage = message, SourceContext = "Nginx.Access",
                RequestId = Guid.TryParse(user, out _) ? user : null,
                Properties = new Dictionary<string, object?>
                {
                    ["ClientIp"] = match.Groups["client"].Value,
                    ["RemoteUser"] = user,
                    ["Request"] = request,
                    ["Method"] = requestParts.Length == 3 ? requestParts[0] : null,
                    ["Target"] = requestParts.Length == 3 ? requestParts[1] : null,
                    ["Protocol"] = requestParts.Length == 3 ? requestParts[2] : null,
                    ["StatusCode"] = status,
                    ["BytesSent"] = long.TryParse(match.Groups["bytes"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var bytes) ? bytes : null,
                    ["Referer"] = match.Groups["referer"].Value,
                    ["UserAgent"] = match.Groups["agent"].Value
                },
                RawJson = line
            };
        }

        match = Error.Match(line);
        if (!match.Success || !DateTimeOffset.TryParseExact(match.Groups["timestamp"].Value,
                "yyyy/MM/dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var errorTimestamp))
            return null;
        var level = match.Groups["level"].Value switch
        {
            "debug" => ViewerLogLevel.Debug,
            "warn" => ViewerLogLevel.Warning,
            "error" => ViewerLogLevel.Error,
            "crit" or "alert" or "emerg" => ViewerLogLevel.Fatal,
            _ => ViewerLogLevel.Information
        };
        return new LogEntry
        {
            Id = $"{fileName}:{lineOffset}", FileName = fileName, LineOffset = lineOffset,
            Timestamp = errorTimestamp, Level = level,
            Message = match.Groups["message"].Value, RenderedMessage = match.Groups["message"].Value,
            SourceContext = "Nginx.Error", RawJson = line
        };
    }
}
