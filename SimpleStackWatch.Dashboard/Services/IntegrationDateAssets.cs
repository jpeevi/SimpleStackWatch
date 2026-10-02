using System.Collections.Concurrent;
using System.Reflection;
using System.Text;

namespace MonitorDashboard.Services;

// Adapt display formats in the pinned database-log and health-history UIs.
// This middleware runs after the dashboard's authentication gate.
public static class IntegrationDateAssets
{
    private static readonly ConcurrentDictionary<string, byte[]> Cache = new(StringComparer.Ordinal);

    public static async Task<bool> TryServeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)) return false;
        string? kind = null;
        var path = context.Request.Path;
        if (path.StartsWithSegments("/integrations/database-logs"))
        {
            if (path.Value!.EndsWith("/assets/index-DbGmgMHM.js", StringComparison.Ordinal)) kind = "database";
            else if (path.Value.EndsWith("/assets/Search-Dd4frzZK.js", StringComparison.Ordinal)) kind = "database-search";
        }
        else if (path == "/integrations/health-resources/healthchecks-bundle.js") kind = "health";
        if (kind is null) return false;

        var bytes = Cache.GetOrAdd(kind, Load);
        context.Response.ContentType = "text/javascript; charset=utf-8";
        context.Response.ContentLength = bytes.Length;
        context.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        return true;
    }

    private static byte[] Load(string kind)
    {
        var assembly = kind == "health"
            ? typeof(HealthChecks.UI.Configuration.Settings).Assembly
            : typeof(Serilog.Ui.Web.Models.UiOptions).Assembly;
        var resource = kind switch
        {
            "health" => "HealthChecks.UI.assets.healthchecks-bundle.js",
            "database-search" => "Serilog.Ui.Web.wwwroot.dist.assets.Search-Dd4frzZK.js",
            _ => "Serilog.Ui.Web.wwwroot.dist.assets.index-DbGmgMHM.js"
        };
        var text = Read(assembly, resource);
        if (kind == "health")
        {
            text = text.Replace(".format(\"LLL\")", ".format(\"DD/MM/YYYY HH:mm:ss\")", StringComparison.Ordinal)
                .Replace(".utc(e.onStateFrom).fromNow().toString()", ".utc(e.onStateFrom).format(\"DD/MM/YYYY HH:mm:ss [UTC]\").toString()", StringComparison.Ordinal)
                .Replace("new Date(e.lastExecuted).toLocaleString()", "stackwatchHealthDate(e.lastExecuted)", StringComparison.Ordinal);
            text += "\nfunction stackwatchHealthDate(value){const t=new Date(value);if(Number.isNaN(t.getTime()))return '—';const p=n=>String(n).padStart(2,'0');return p(t.getDate())+'/'+p(t.getMonth()+1)+'/'+t.getFullYear()+' '+p(t.getHours())+':'+p(t.getMinutes())+':'+p(t.getSeconds());}\n";
        }
        else if (kind == "database-search")
        {
            text = text.Replace("label:\"Start date\",clearable", "label:\"Start date\",valueFormat:\"DD/MM/YYYY HH:mm:ss\",clearable", StringComparison.Ordinal)
                .Replace("label:\"End date\",clearable", "label:\"End date\",valueFormat:\"DD/MM/YYYY HH:mm:ss\",clearable", StringComparison.Ordinal);
        }
        else
        {
            text = text.Replace("\"ll HH:mm:ss\"", "\"DD/MM/YYYY HH:mm:ss\"", StringComparison.Ordinal)
                .Replace("\"ll HH:mm:ss [[UTC]]\"", "\"DD/MM/YYYY HH:mm:ss [[UTC]]\"", StringComparison.Ordinal)
                .Replace(".format(\"ll\")", ".format(\"DD/MM/YYYY\")", StringComparison.Ordinal);
        }
        return Encoding.UTF8.GetBytes(text);
    }

    private static string Read(Assembly assembly, string resource)
    {
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"Missing integration resource: {resource}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
