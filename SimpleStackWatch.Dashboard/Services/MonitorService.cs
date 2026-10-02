using System.Net.Http.Headers;
using MonitorDashboard.Models;
namespace MonitorDashboard.Services;
public sealed class MonitorService(DashboardOptions options, IHttpClientFactory factory)
{
    public async Task<string> ReadAsync(string endpoint, int? pid, CancellationToken ct)
    {
        if (!options.Processes.Monitor.Enabled) throw new InvalidOperationException("dotnet-monitor is not configured.");
        if (endpoint is not ("processes" or "metrics")) throw new InvalidOperationException("Unsupported diagnostics endpoint.");
        var baseUri = new Uri(options.Processes.Monitor.BaseUrl.TrimEnd('/') + "/");
        if (baseUri.Scheme is not ("http" or "https")) throw new InvalidOperationException("Invalid dotnet-monitor URL.");
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(baseUri, endpoint + (pid is null ? "" : $"?pid={pid}")));
        if (!string.IsNullOrWhiteSpace(options.Processes.Monitor.ApiKey)) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Processes.Monitor.ApiKey);
        var client = factory.CreateClient("monitor");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var reader = new StreamReader(stream);
        var buffer = new char[4096];
        var output = new System.Text.StringBuilder();
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), ct)) > 0)
        {
            if (output.Length + count > 2 * 1024 * 1024) throw new InvalidOperationException("Monitor response exceeded the size limit.");
            output.Append(buffer, 0, count);
        }
        return output.ToString();
    }
}
