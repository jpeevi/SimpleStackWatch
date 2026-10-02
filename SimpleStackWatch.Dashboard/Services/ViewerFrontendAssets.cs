using System.Collections.Concurrent;
using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.StaticFiles;
using Serilog.Viewer;

namespace MonitorDashboard.Services;

// Serve only the package's allowlisted embedded frontend resources.
// API requests continue into the isolated folder's package endpoints.
public sealed class ViewerFrontendAssets(string basePath, IWebHostEnvironment environment)
{
    private const string ResourcePrefix = "Serilog.Viewer.wwwroot.";
    private const string MainBundle = "assets/index-CL8Bh9M1.js";
    private static readonly Assembly ViewerAssembly = typeof(LogViewerExtensions).Assembly;
    private static readonly HashSet<string> Resources = new(ViewerAssembly.GetManifestResourceNames(), StringComparer.Ordinal);
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();
    private readonly ConcurrentDictionary<string, Asset> cache = new(StringComparer.Ordinal);

    public async Task<bool> TryServeAsync(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
            return false;
        if (!context.Request.Path.StartsWithSegments(new PathString(basePath), out var remaining))
            return false;
        if (remaining.StartsWithSegments("/api") || remaining.StartsWithSegments("/hubs"))
            return false;

        var relative = remaining.Value?.TrimStart('/') ?? "";
        if (relative == "log-viewer-icon.png")
            relative = "assets/log-viewer-icon-Bu7U9x5x.png";
        else if (!remaining.StartsWithSegments("/assets") && !Path.HasExtension(relative))
            relative = "index.html";

        var resourceName = ResourcePrefix + relative.Replace('/', '.');
        if (!Resources.Contains(resourceName))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return true;
        }

        var asset = cache.GetOrAdd(relative, Load);
        context.Response.ContentType = asset.ContentType;
        context.Response.ContentLength = asset.Bytes.Length;
        context.Response.Headers.CacheControl = "no-store";
        if (!HttpMethods.IsHead(context.Request.Method))
            await context.Response.Body.WriteAsync(asset.Bytes, context.RequestAborted);
        return true;
    }

    private Asset Load(string relative)
    {
        using var resource = ViewerAssembly.GetManifestResourceStream(ResourcePrefix + relative.Replace('/', '.'))
            ?? throw new InvalidOperationException($"Missing viewer resource: {relative}");
        using var output = new MemoryStream();
        resource.CopyTo(output);

        // Serilog.Viewer 1.3.2's net9.0 assembly contains a zero-length main
        // bundle. Use the identical asset from its net8.0 assembly if needed.
        if (relative == MainBundle && output.Length == 0)
        {
            var fallback = environment.WebRootFileProvider.GetFileInfo("vendor/serilog-viewer/index-CL8Bh9M1.js");
            if (!fallback.Exists)
                throw new InvalidOperationException("The Serilog.Viewer fallback JavaScript bundle is missing. Copy wwwroot/vendor/serilog-viewer from the update.");
            using var file = fallback.CreateReadStream();
            file.CopyTo(output);
        }

        if (!ContentTypes.TryGetContentType(relative, out var contentType))
            contentType = "application/octet-stream";
        var bytes = output.ToArray();
        if (relative.EndsWith(".html", StringComparison.OrdinalIgnoreCase) ||
            relative.EndsWith(".js", StringComparison.OrdinalIgnoreCase) ||
            relative.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
        {
            // This also fixes the React router basename, API URLs, image
            // URLs, and optional hub URL embedded in the JavaScript bundle.
            var text = Encoding.UTF8.GetString(bytes)
                .Replace("/logviewer", basePath, StringComparison.Ordinal);
            if (relative == MainBundle)
            {
                // Keep ISO values in APIs and <time datetime> attributes;
                // customize only user-visible formatting and date inputs.
                text = text
                    .Replace("yyyy-MM-dd HH:mm:ss.SSS", "dd/MM/yyyy HH:mm:ss", StringComparison.Ordinal)
                    .Replace("dateTime:z,title:z", "dateTime:z,title:m", StringComparison.Ordinal)
                    .Replace("children:f?T1(s,{addSuffix:!0}):m", "children:m", StringComparison.Ordinal)
                    .Replace("T1(new Date(c.lastModified),{addSuffix:!0})", "oi(new Date(c.lastModified),\"dd/MM/yyyy HH:mm:ss\")", StringComparison.Ordinal)
                    .Replace("oi(new Date(m.timestamp),\"HH:mm\")", "oi(new Date(m.timestamp),\"dd/MM/yyyy HH:mm:ss\")", StringComparison.Ordinal)
                    .Replace("oi(new Date(m.timestamp),\"MMM dd\")", "oi(new Date(m.timestamp),\"dd/MM/yyyy HH:mm:ss\")", StringComparison.Ordinal)
                    .Replace("d.jsx(\"input\",{type:\"datetime-local\"", "d.jsx(StackWatchDateInput,{type:\"text\"", StringComparison.Ordinal);
                var customizations = environment.WebRootFileProvider.GetFileInfo("js/viewer-customizations.js");
                if (!customizations.Exists)
                    throw new InvalidOperationException("Missing wwwroot/js/viewer-customizations.js.");
                using var customizationStream = customizations.CreateReadStream();
                using var reader = new StreamReader(customizationStream);
                text += "\n" + reader.ReadToEnd();
            }
            bytes = Encoding.UTF8.GetBytes(text);
            contentType += "; charset=utf-8";
        }
        return new Asset(bytes, contentType);
    }

    private sealed record Asset(byte[] Bytes, string ContentType);
}
