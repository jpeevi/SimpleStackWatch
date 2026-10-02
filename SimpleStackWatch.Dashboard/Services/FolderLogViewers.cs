using System.Diagnostics;
using System.Text.RegularExpressions;
using MonitorDashboard.Models;
using Serilog.Viewer;
using Serilog.Viewer.Interfaces;

namespace MonitorDashboard.Services;

// Serilog.Viewer has one folder per service provider. Keep each folder's
// options, index, watcher, and endpoint routes in its own provider.
public sealed class FolderLogViewers : IAsyncDisposable
{
    private readonly List<(PathString Prefix, RequestDelegate Pipeline)> pipelines = [];
    private readonly List<ServiceProvider> providers = [];

    public static void Configure(LogOptions logs, string contentRoot)
    {
        if (logs.Folders.Count == 0)
        {
            logs.Folders.Add(new LogFolderOptions
            {
                Id = "default", Name = "Application logs", Folder = logs.Folder
            });
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in logs.Folders)
        {
            if (!Regex.IsMatch(folder.Id, "^[a-z0-9][a-z0-9-]{0,63}$") || !ids.Add(folder.Id))
                throw new InvalidOperationException("Log folder IDs must be unique lowercase names containing letters, digits, or hyphens.");
            if (string.IsNullOrWhiteSpace(folder.Folder))
                throw new InvalidOperationException($"A path is required for log folder '{folder.Id}'.");

            folder.Folder = Path.GetFullPath(folder.Folder, contentRoot);
            if (!Directory.Exists(folder.Folder))
                throw new InvalidOperationException($"Log folder '{folder.Id}' does not exist: {folder.Folder}. Configure an existing directory or disable the file viewer.");
            if (string.IsNullOrWhiteSpace(folder.Name)) folder.Name = folder.Id;
        }
    }

    public FolderLogViewers(IServiceProvider root, DashboardOptions options)
    {
        try
        {
            foreach (var folder in options.Logs.Folders)
            {
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddSingleton(root.GetRequiredService<ILoggerFactory>());
                services.AddRouting();
                services.AddMetrics();
                services.AddSingleton(root.GetRequiredService<IWebHostEnvironment>());
                services.AddSingleton<IHostEnvironment>(root.GetRequiredService<IWebHostEnvironment>());
                services.AddSingleton(root.GetRequiredService<IConfiguration>());
                services.AddSingleton(root.GetRequiredService<DiagnosticListener>());
                services.AddSingleton<DiagnosticSource>(root.GetRequiredService<DiagnosticListener>());
                // Custom parsers precede the package's default text parser.
                services.AddSingleton<ILogParser, NginxLogParser>();
                services.AddSingleton<ILogParser, DashboardLogParser>();
                services.AddLogViewer(viewer =>
                {
                    viewer.LogFolder = folder.Folder;
                    viewer.BasePath = folder.BasePath;
                    // The parent dashboard authenticates every request first.
                    viewer.EnableBasicAuth = false;
                    viewer.EnableFileDelete = false;
                    viewer.EnableFileDownload = true;
                });

                var provider = services.BuildServiceProvider();
                providers.Add(provider);
                // Use a fresh builder so routes do not leak between folders.
                var branch = new ApplicationBuilder(provider);
                branch.Use(async (context, next) =>
                {
                    var originalServices = context.RequestServices;
                    await using var scope = provider.CreateAsyncScope();
                    context.RequestServices = scope.ServiceProvider;
                    try { await next(context); }
                    finally { context.RequestServices = originalServices; }
                });
                // The package's frontend hard-codes /logviewer. Rebase its
                // HTML and bundles without sharing folder state between tabs.
                var assets = new ViewerFrontendAssets(
                    folder.BasePath, root.GetRequiredService<IWebHostEnvironment>());
                branch.Use(async (context, next) =>
                {
                    if (!await assets.TryServeAsync(context)) await next(context);
                });
                branch.UseRouting();
                branch.UseLogViewer();
                branch.UseEndpoints(endpoints => endpoints.MapLogViewer());
                pipelines.Add((new PathString(folder.BasePath), branch.Build()));
            }
        }
        catch
        {
            foreach (var provider in providers) provider.Dispose();
            throw;
        }
    }

    public bool TryGetPipeline(PathString path, out RequestDelegate? pipeline)
    {
        foreach (var entry in pipelines)
        {
            if (!path.StartsWithSegments(entry.Prefix)) continue;
            pipeline = entry.Pipeline;
            return true;
        }
        pipeline = null;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var provider in providers) await provider.DisposeAsync();
    }
}
