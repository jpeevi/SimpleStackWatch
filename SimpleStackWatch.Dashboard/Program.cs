using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading.RateLimiting;

using HealthChecks.UI.Client;

using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

using MonitorDashboard.Auth;
using MonitorDashboard.Models;
using MonitorDashboard.Services;

using Serilog;
using Serilog.Debugging;
using Serilog.Ui.Core.Extensions;
using Serilog.Ui.PostgreSqlProvider.Extensions;
using Serilog.Ui.Web.Extensions;


// ============================================================
// Application Configuration
// ============================================================

// Account commands share the web application's configuration,
// identity database, and Data Protection key directory.
var cli = args.Length > 0 && args[0] == "users";
var builder = WebApplication.CreateBuilder(cli ? [] : args);

// Load shared defaults first, then the selected environment file.
// ASPNETCORE_ENVIRONMENT=Development loads appsettings.Development.json.
// ASPNETCORE_ENVIRONMENT=Uat loads appsettings.Uat.json.
// Environment variables override values from both JSON files.
builder.Configuration
    .AddJsonFile(
        "appsettings.json",
        optional: false,
        reloadOnChange: true)
    .AddJsonFile(
        $"appsettings.{builder.Environment.EnvironmentName}.json",
        optional: true,
        reloadOnChange: true)
    .AddEnvironmentVariables();

// Retain command-line overrides for the web application without
// treating account-management arguments as configuration values.
if (!cli && args.Length > 0)
{
    builder.Configuration.AddCommandLine(args);
}

var configuration = builder.Configuration;
var environment = builder.Environment;

// ============================================================
// Logging
// ============================================================

builder.Logging.ClearProviders();

var configuredLogPath = configuration["LogSettings:FilePath"] ?? "logs/app-.log";
if (string.IsNullOrWhiteSpace(configuredLogPath))
    throw new InvalidOperationException("LogSettings:FilePath must contain a log file path.");
var logPath = Path.IsPathRooted(configuredLogPath)
    ? configuredLogPath
    : Path.Combine(environment.ContentRootPath, configuredLogPath);

try
{
    var logDirectory = Path.GetDirectoryName(logPath);
    if (!string.IsNullOrWhiteSpace(logDirectory)) Directory.CreateDirectory(logDirectory);

    SelfLog.Enable(message => Console.Error.WriteLine($"SERILOG INTERNAL ERROR: {message}"));

    const string logTemplate = "{Timestamp:dd/MM/yyyy HH:mm:ss zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";
    Log.Logger = new LoggerConfiguration()
        .ReadFrom.Configuration(configuration)
        .Enrich.FromLogContext()
        .Enrich.WithProperty("Application", environment.ApplicationName)
        .Enrich.WithProperty("Environment", environment.EnvironmentName)
        .WriteTo.Console(outputTemplate: logTemplate, formatProvider: CultureInfo.InvariantCulture)
        .WriteTo.File(logPath,
            outputTemplate: logTemplate,
            formatProvider: CultureInfo.InvariantCulture,
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 30,
            shared: true,
            flushToDiskInterval: TimeSpan.FromSeconds(1))
        .CreateLogger();

    // The logger is flushed by the application lifetime finally block.
    builder.Host.UseSerilog(Log.Logger, dispose: false);
}
catch (Exception exception)
{
    Console.Error.WriteLine($"Failed to configure Serilog: {exception}");
    Log.CloseAndFlush();
    throw;
}

try
{

    var options = configuration
        .GetSection("Dashboard")
        .Get<DashboardOptions>() ?? new();

    PostgresService.Configure(options.Postgres);

    options.DataDirectory = Path.GetFullPath(
        options.DataDirectory,
        environment.ContentRootPath);

    options.Logs.Folder = Path.GetFullPath(
        options.Logs.Folder,
        environment.ContentRootPath);

    builder.Services.AddSingleton(options);

    const string healthPath = "/healthz";


    // ============================================================
    // Authentication Storage and Data Protection
    // ============================================================

    Directory.CreateDirectory(options.DataDirectory);

    var keys = Path.Combine(options.DataDirectory, "keys");
    Directory.CreateDirectory(keys);

    // Restrict access to the identity database and key ring on Linux.
    if (OperatingSystem.IsLinux())
    {
        var privateDirectory =
            UnixFileMode.UserRead |
            UnixFileMode.UserWrite |
            UnixFileMode.UserExecute;

        File.SetUnixFileMode(options.DataDirectory, privateDirectory);
        File.SetUnixFileMode(keys, privateDirectory);
    }

    // Keep this application name consistent between the CLI and web
    // service so they can read the same protected Identity tokens.
    builder.Services
        .AddDataProtection()
        .SetApplicationName("SimpleStackWatchDashboard")
        .PersistKeysToFileSystem(new DirectoryInfo(keys));

    var databasePath = Path.Combine(options.DataDirectory, "users.db");

    var identityConnectionString =
        new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString();

    builder.Services.AddDbContext<AuthDbContext>(db =>
        db.UseSqlite(identityConnectionString));


    // ============================================================
    // Identity and Account Security
    // ============================================================

    builder.Services
        .AddIdentity<DashboardUser, IdentityRole>(identity =>
        {
            identity.Password.RequiredLength = 12;
            identity.Password.RequireNonAlphanumeric = false;
            identity.Password.RequireUppercase = false;
            identity.Password.RequireLowercase = false;
            identity.Password.RequireDigit = false;

            identity.Lockout.DefaultLockoutTimeSpan =
                TimeSpan.FromMinutes(15);

            identity.Lockout.MaxFailedAccessAttempts = 5;
            identity.Lockout.AllowedForNewUsers = true;
        })
        .AddEntityFrameworkStores<AuthDbContext>()
        .AddDefaultTokenProviders();

    // Check security stamps on each authenticated request so CLI
    // password/MFA resets invalidate existing sessions promptly.
    builder.Services.Configure<SecurityStampValidatorOptions>(security =>
        security.ValidationInterval = TimeSpan.Zero);


    // ============================================================
    // Authentication Cookie
    // ============================================================

    builder.Services.ConfigureApplicationCookie(cookie =>
    {
        cookie.Cookie.Name = "SimpleStackWatchDashboard.Auth";
        cookie.Cookie.HttpOnly = true;
        cookie.Cookie.SameSite = SameSiteMode.Strict;
        cookie.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;

        cookie.LoginPath = "/account/login";
        cookie.ExpireTimeSpan = TimeSpan.FromHours(8);
        cookie.SlidingExpiration = false;

        // JSON requests receive 401 rather than a login-page redirect.
        cookie.Events.OnRedirectToLogin = context =>
        {
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }
            else
            {
                context.Response.Redirect(context.RedirectUri);
            }

            return Task.CompletedTask;
        };
    });


    // ============================================================
    // Local Nginx Reverse Proxy
    // ============================================================

    builder.Services.Configure<ForwardedHeadersOptions>(proxy =>
    {
        proxy.ForwardedHeaders =
            ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        proxy.ForwardLimit = 1;

        // Accept forwarding information only from Nginx on this machine.
        // Use the .NET 10 KnownIPNetworks API to remove default network ranges.
        proxy.KnownIPNetworks.Clear();
        proxy.KnownProxies.Clear();
        proxy.KnownProxies.Add(IPAddress.Loopback);
        proxy.KnownProxies.Add(IPAddress.IPv6Loopback);
    });


    // ============================================================
    // MVC, Authorization, and Anti-forgery
    // ============================================================

    builder.Services.AddAuthorization(authorization =>
    {
        authorization.FallbackPolicy = new AuthorizationPolicyBuilder()
            .RequireAuthenticatedUser()
            .Build();
    });

    builder.Services.AddAntiforgery(antiforgery =>
        antiforgery.HeaderName = "X-CSRF-TOKEN");

    builder.Services.AddControllersWithViews(mvc =>
    {
        mvc.Filters.Add(new AutoValidateAntiforgeryTokenAttribute());
    });


    // ============================================================
    // Login Rate Limiting
    // ============================================================

    builder.Services.AddRateLimiter(limiter =>
    {
        limiter.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

        limiter.AddPolicy("login", context =>
            RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = Math.Clamp(
                        options.LoginRequestsPerMinute,
                        10,
                        200),
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0
                }));
    });


    // ============================================================
    // Log Viewer Registration
    // ============================================================

    // CLI commands do not start monitoring services or log viewers.
    if (!cli && options.Logs.EnableFileViewer)
    {
        // Keep the original single-folder setting as a fallback.
        FolderLogViewers.Configure(options.Logs, environment.ContentRootPath);

        // Make the application's own configured log folder selectable too.
        var applicationLogDirectory = Path.GetDirectoryName(Path.GetFullPath(logPath))!;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!options.Logs.Folders.Any(folder => string.Equals(
                Path.TrimEndingDirectorySeparator(folder.Folder),
                Path.TrimEndingDirectorySeparator(applicationLogDirectory), pathComparison)))
        {
            var folderId = "stackwatch";
            for (var suffix = 2; options.Logs.Folders.Any(folder => folder.Id == folderId); suffix++)
                folderId = $"stackwatch-{suffix}";
            options.Logs.Folders.Add(new LogFolderOptions
            {
                Id = folderId, Name = "SimpleStackWatch", Folder = applicationLogDirectory
            });
        }

        builder.Services.AddSingleton<FolderLogViewers>();
    }

    if (!cli && options.Logs.Database.Enabled)
    {
        if (string.IsNullOrWhiteSpace(options.Logs.Database.ConnectionString))
        {
            throw new InvalidOperationException(
                "A database log connection string is required.");
        }

        builder.Services.AddSerilogUi(ui => ui.UseNpgSql(postgres => postgres
            .WithConnectionString(options.Logs.Database.ConnectionString)
            .WithTable(options.Logs.Database.Table)
            .WithSchema(options.Logs.Database.Schema)));
    }


    // ============================================================
    // PostgreSQL and Redis Health Checks
    // ============================================================

    // HealthChecks.UI uses this process-local credential to poll
    // /healthz without requiring a browser login cookie.
    var healthKey = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    if (!cli)
    {
        var health = builder.Services.AddHealthChecks();

        for (var index = 0; index < options.Postgres.Databases.Count; index++)
        {
            var database = options.Postgres.Databases[index];
            if (string.IsNullOrWhiteSpace(database.ConnectionString)) continue;
            health.AddNpgSql(
                database.ConnectionString,
                name: $"postgres: {database.Name}",
                tags: ["postgres", $"postgres:{index}"],
                timeout: TimeSpan.FromSeconds(5));
        }

        if (!string.IsNullOrWhiteSpace(options.Redis.ConnectionString))
        {
            health.AddRedis(
                options.Redis.ConnectionString,
                name: "redis",
                tags: ["redis"],
                timeout: TimeSpan.FromSeconds(5));
        }

        builder.Services
            .AddHealthChecksUI(ui =>
            {
                ui.AddHealthCheckEndpoint("PostgreSQL & Redis", healthPath);
                ui.SetEvaluationTimeInSeconds(
                    Math.Clamp(options.RefreshSeconds, 5, 300));
                ui.MaximumHistoryEntriesPerEndpoint(50);

                ui.ConfigureApiEndpointHttpclient((_, client) =>
                    client.DefaultRequestHeaders.Add("X-Health-Key", healthKey));
            })
            .AddInMemoryStorage();
    }


    // ============================================================
    // Monitoring Services and Outbound HTTP
    // ============================================================

    if (!cli)
    {
        builder.Services.AddSingleton<ProcessService>();
        builder.Services.AddSingleton<PostgresService>();
        builder.Services.AddSingleton<RedisService>();
        builder.Services.AddSingleton<AwsService>();
        builder.Services.AddSingleton<MonitorService>();

        builder.Services
            .AddHttpClient("monitor", client =>
                client.Timeout = TimeSpan.FromSeconds(15))
            .ConfigurePrimaryHttpMessageHandler(() =>
                new HttpClientHandler { AllowAutoRedirect = false });
    }


    // ============================================================
    // Build Application and Initialize Identity Storage
    // ============================================================

    var app = builder.Build();

    using (var scope = app.Services.CreateScope())
    {
        await scope.ServiceProvider
            .GetRequiredService<AuthDbContext>()
            .Database.EnsureCreatedAsync();
    }

    if (OperatingSystem.IsLinux())
    {
        File.SetUnixFileMode(
            databasePath,
            UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }


    // ============================================================
    // CLI Account Management
    // ============================================================

    if (cli)
    {
        Environment.ExitCode = await UserCommands.RunAsync(app.Services, args);
        await app.DisposeAsync();
        return;
    }


    // ============================================================
    // Forwarded Request Scheme and Client IP
    // ============================================================

    // Run before HSTS, authentication/cookies, and IP-based rate limiting.
    // Nginx already preserves Host; X-Forwarded-Host is not required here.
    app.UseForwardedHeaders();


    // ============================================================
    // Security Headers and Error Handling
    // ============================================================

    if (!app.Environment.IsDevelopment())
    {
        app.UseHsts();
    }

    app.Use(async (context, next) =>
    {
        context.Response.Headers["X-Content-Type-Options"] = "nosniff";
        context.Response.Headers["X-Frame-Options"] = "SAMEORIGIN";
        context.Response.Headers["Referrer-Policy"] = "same-origin";
        context.Response.Headers.CacheControl = "no-store";

        try
        {
            await next(context);
        }
        catch (Exception ex) when (
            !context.Response.HasStarted &&
            ex is not OperationCanceledException)
        {
            app.Logger.LogError(ex,
                "Unhandled request exception for {Method} {Path}; trace {TraceId}",
                context.Request.Method, context.Request.Path.Value, context.TraceIdentifier);
            // Avoid returning exception details or connection secrets.
            context.Response.Clear();
            context.Response.StatusCode = StatusCodes.Status500InternalServerError;
            context.Response.ContentType = "text/plain; charset=utf-8";
            context.Response.Headers.CacheControl = "no-store";

            await context.Response.WriteAsync(
                "The request could not be completed. Check server configuration.");
        }
    });


    // ============================================================
    // Request Middleware
    // ============================================================

    app.UseStaticFiles();
    app.UseRouting();
    app.UseRateLimiter();
    app.UseAuthentication();


    // ============================================================
    // Health Endpoint and Monitoring Access Gate
    // ============================================================

    // Middleware-based viewers do not inherit MVC authorization.
    // Authenticate requests before any integration middleware runs.
    app.Use(async (context, next) =>
    {
        var path = context.Request.Path;

        if (path == healthPath)
        {
            var supplied = context.Request.Headers["X-Health-Key"].ToString();

            var validHealthKey = CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(supplied),
                Encoding.UTF8.GetBytes(healthKey));

            if (!validHealthKey)
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(context);
            return;
        }

        // Account actions apply their own anonymous/authorized policies.
        if (path.StartsWithSegments("/account"))
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            if (path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            }
            else
            {
                context.Response.Redirect("/account/login");
            }

            return;
        }

        var userManager = context.RequestServices
            .GetRequiredService<UserManager<DashboardUser>>();

        var user = await userManager.GetUserAsync(context.User);

        if (user is not { Enabled: true, TwoFactorEnabled: true })
        {
            await context.RequestServices
                .GetRequiredService<SignInManager<DashboardUser>>()
                .SignOutAsync();

            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        // Keep integration viewers read-only, including package APIs.
        if (path.StartsWithSegments("/integrations") &&
            !HttpMethods.IsGet(context.Request.Method) &&
            !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        await next(context);
    });

    app.UseAuthorization();


    // ============================================================
    // Log Viewer Middleware
    // ============================================================

    app.Use(async (context, next) =>
    {
        if (!await IntegrationDateAssets.TryServeAsync(context)) await next(context);
    });

    if (options.Logs.EnableFileViewer)
    {
        var viewers = app.Services.GetRequiredService<FolderLogViewers>();
        app.Use(async (context, next) =>
        {
            if (viewers.TryGetPipeline(context.Request.Path, out var pipeline))
            {
                await pipeline!(context);
                return;
            }

            await next(context);
        });
    }

    if (options.Logs.Database.Enabled)
    {
        // UiOptions exposes fluent configuration methods; its property
        // setters are not publicly accessible in this package version.
        app.UseSerilogUi(ui => ui
            .WithRoutePrefix("integrations/database-logs")
            .WithHomeUrl("/dashboard/logs"));
    }


    // ============================================================
    // Endpoints
    // ============================================================

    // Use ASP.NET Core's standard health-check endpoint mapping.
    // AllowAnonymous bypasses cookie authorization only: the access
    // gate above still requires the private X-Health-Key credential.
    app.MapHealthChecks(healthPath, new HealthCheckOptions
    {
        ResponseWriter = UIResponseWriter.WriteHealthCheckUIResponse
    }).AllowAnonymous();

    app.MapHealthChecksUI(ui =>
    {
        ui.UIPath = "/integrations/health";
        ui.ResourcesPath = "/integrations/health-resources";
        ui.ApiPath = "/integrations/health-api";
        ui.WebhookPath = "/integrations/health-webhooks";

        // Keep the leading slash in generated HTML URLs. Relative paths
        // would resolve beneath /integrations/ and duplicate that segment.
        ui.UseRelativeResourcesPath = false;
        ui.UseRelativeApiPath = false;
        ui.UseRelativeWebhookPath = false;
    }).RequireAuthorization();

    app.MapControllers();


    // ============================================================
    // Application Lifetime
    // ============================================================

    await app.RunAsync();
}
catch (Exception exception)
{
    Log.Fatal(exception, "SimpleStackWatch stopped unexpectedly");
    throw;
}
finally
{
    await Log.CloseAndFlushAsync();
    SelfLog.Disable();
}

public partial class Program { }
