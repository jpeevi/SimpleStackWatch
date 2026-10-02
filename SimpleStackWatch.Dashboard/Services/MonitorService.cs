using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using MonitorDashboard.Models;

namespace MonitorDashboard.Services;

public sealed class MonitorService(
    DashboardOptions options,
    IHttpClientFactory factory)
{
    public async Task<string> ReadAsync(
        string endpoint,
        int? pid,
        CancellationToken ct,
        Guid? uid = null)
    {
        var settings = options.Processes.Monitor;

        if (!settings.Enabled)
        {
            throw new InvalidOperationException(
                "dotnet-monitor is not configured.");
        }

        if (endpoint is not ("processes" or "metrics"))
        {
            throw new InvalidOperationException(
                "Unsupported diagnostics endpoint.");
        }

        if (!Uri.TryCreate(
                settings.BaseUrl.TrimEnd('/') + "/",
                UriKind.Absolute,
                out var baseUri) ||
            baseUri.Scheme is not ("http" or "https"))
        {
            throw new InvalidOperationException(
                "Invalid dotnet-monitor URL.");
        }

        string route;

        if (endpoint == "metrics")
        {
            if (!uid.HasValue || uid.Value == Guid.Empty)
            {
                throw new InvalidOperationException(
                    "Select an application before collecting metrics.");
            }

            // Runtime IDs distinguish applications even when OS PIDs are reused.
            route = $"livemetrics?uid={uid.Value:D}&durationSeconds=10";
        }
        else
        {
            route = "processes";

            if (pid.HasValue)
            {
                route += "?pid=" +
                    pid.Value.ToString(CultureInfo.InvariantCulture);
            }
        }

        // Processes and live metrics both use the authenticated API listener.
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            new Uri(baseUri, route));

        if (!string.IsNullOrWhiteSpace(settings.ApiKey))
        {
            request.Headers.Authorization =
                new AuthenticationHeaderValue("Bearer", settings.ApiKey);
        }

        var client = factory.CreateClient("monitor");

        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            ct);

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);

        var output = new StringBuilder();
        var buffer = new char[4096];
        int count;

        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (output.Length + count > 2 * 1024 * 1024)
            {
                throw new InvalidOperationException(
                    "Monitor response exceeded the size limit.");
            }

            output.Append(buffer, 0, count);
        }

        return output.ToString();
    }
}