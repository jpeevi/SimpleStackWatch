using System.Globalization;
using System.Text.RegularExpressions;
using Serilog.Viewer.Interfaces;
using Serilog.Viewer.Models;
using ViewerLogLevel = Serilog.Viewer.Models.LogLevel;

namespace MonitorDashboard.Services;

// Parse the application sink's explicitly day-first output template.
public sealed class DashboardLogParser : ILogParser
{
    private static readonly Regex Header = new(
        "^(?<timestamp>[0-9]{2}/[0-9]{2}/[0-9]{4} [0-9]{2}:[0-9]{2}:[0-9]{2} [+-][0-9]{2}:[0-9]{2}) \\[(?<level>VRB|DBG|INF|WRN|ERR|FTL)\\] (?<message>.*)$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking,
        TimeSpan.FromMilliseconds(250));
    public LogFileFormat Format => LogFileFormat.PlainText;
    public bool CanParse(string line) => !string.IsNullOrWhiteSpace(line) && Header.IsMatch(line);

    public LogEntry? Parse(string line, string fileName, long lineOffset)
    {
        var match = Header.Match(line);
        if (!match.Success || !DateTimeOffset.TryParseExact(match.Groups["timestamp"].Value,
                "dd/MM/yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var timestamp))
            return null;
        return new LogEntry
        {
            Id = $"{fileName}:{lineOffset}", FileName = fileName, LineOffset = lineOffset,
            Timestamp = timestamp,
            Level = match.Groups["level"].Value switch
            {
                "VRB" => ViewerLogLevel.Verbose, "DBG" => ViewerLogLevel.Debug, "WRN" => ViewerLogLevel.Warning,
                "ERR" => ViewerLogLevel.Error, "FTL" => ViewerLogLevel.Fatal, _ => ViewerLogLevel.Information
            },
            Message = match.Groups["message"].Value, RenderedMessage = match.Groups["message"].Value,
            SourceContext = "SimpleStackWatch", RawJson = line
        };
    }
}
