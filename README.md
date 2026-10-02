# SimpleStackWatch

A simple .NET 10 dashboard for **development and UAT applications running as a single instance**. Kubernetes support is coming soon.

Built with ASP.NET Core, Bootstrap 5, and ES6. The left hamburger menu opens Logs, .NET processes, PostgreSQL, Redis, and EC2. The dashboard opens on Logs and supports dark mode. Displayed dates use `dd/MM/yyyy HH:mm:ss`; source logs, exports, and machine-readable timestamps keep their original format.

Users sign in with a password and Google Authenticator. Accounts and resets are managed from an interactive CLI. There is no browser registration page, email/phone workflow, or super-admin account.

## Run locally

Install the .NET 10 SDK. Run these commands from the solution directory containing `SimpleStackWatch.Dashboard`:

```bash
mkdir -p SimpleStackWatch.Dashboard/logs
dotnet restore SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj
dotnet build SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj -c Debug
dotnet tool install --global dotnet-counters --version 10.0.731102

dotnet run --project SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj -- users add peevi
dotnet run --project SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj
```

Use the full `.csproj` path: the uploaded project also contains an older `MonitorDashboard.csproj`, so selecting only the directory is ambiguous. If dotnet-counters is already installed, use that installation.

Open [http://127.0.0.1:5080](http://127.0.0.1:5080). Configure log folders before starting; their directories must exist and be accessible.

Account creation prompts for a password, displays a manual authenticator key, verifies a code, and prints ten single-use recovery codes. In Google Authenticator, choose **Enter a setup key → Time based**. Keep the recovery codes somewhere private. The account becomes active only after verification.

## Configuration

The application loads `appsettings.json`, then `appsettings.Development.json` or `appsettings.Uat.json` according to the environment. Environment variables override JSON; web command-line configuration overrides are applied last. Restart after changes. The systemd setup below uses **Development**; choose `Uat` to load `appsettings.Uat.json` instead.

Keep hostnames, paths, database display names, and monitoring options in appsettings. Keep connection strings and credentials in environment variables.

These are the relevant settings to merge into your existing JSON:

```json
{
  "Urls": "http://127.0.0.1:5080",
  "AllowedHosts": "localhost;127.0.0.1;stackwatch.lumestack.local",
  "Dashboard": {
    "DataDirectory": "data",
    "Processes": {
      "CountersExecutable": "dotnet-counters"
    },
    "Postgres": {
      "Databases": [
        { "Name": "DevSolution" }
      ],
      "ShowQueryText": false,
      "MaximumRows": 100
    }
  },
  "LogSettings": {
    "FilePath": "logs/app-.log"
  }
}
```

This is a merge example, not a replacement for the complete configuration. Update the environment-specific JSON too if it overrides these properties.

Relative paths resolve from the application root. With the systemd working directory `/opt/stackwatch`, `data` resolves to `/opt/stackwatch/data` and `logs/app-.log` resolves to `/opt/stackwatch/logs/app-.log`. An absolute `Dashboard:DataDirectory` is also supported.

`DataDirectory` contains `users.db` and the `keys` directory. Back up both together: the keys protect stored MFA secrets. Keep this directory outside `wwwroot` and out of source control.

## Secrets and multiple databases

For local Rider/dotnet-run development, set variables in the selected profile's `Properties/launchSettings.json` or export them in the shell. systemd does **not** read launchSettings; use `/etc/stackwatch/systemd.env` for the service.

Environment-file syntax is `KEY=value`, without JSON commas or `export`:

```dotenv
Dashboard__Postgres__Databases__0__ConnectionString="Host=127.0.0.1;Port=5432;Database=devsolution;Username=monitor;Password=YOUR_PASSWORD;Timeout=5;Command Timeout=5"
Dashboard__Redis__ConnectionString="127.0.0.1:6379,connectTimeout=5000,asyncTimeout=5000,allowAdmin=true"
Dashboard__Aws__Enabled=false
Dashboard__Aws__Region=ap-south-1
Dashboard__Aws__MetricWindowMinutes=15
Dashboard__Aws__MetricPeriodSeconds=300
```

Replace the example credentials. For password-protected Redis, add `password=YOUR_REDIS_PASSWORD` to its connection string.

For another PostgreSQL database, append a display name to `Dashboard:Postgres:Databases` and set `Dashboard__Postgres__Databases__1__ConnectionString`. Index 0 maps to the first name, index 1 to the second, and so on. Keep names unique and list order consistent across JSON files.

PostgreSQL connection strings are read from environment variables only. A missing indexed variable leaves that database unconfigured. The previous `Dashboard__Postgres__ConnectionString` variable is supported only when the database list is empty or absent.

The PostgreSQL selector switches both the session table and health badge. Sessions are scoped to the selected connection's database, excluding the dashboard's own backend PID. Query text is off by default. To see other users' session details, the monitoring account needs suitable PostgreSQL permissions, such as the `pg_monitor` role. Monitoring cannot cancel or terminate sessions.

## Logs

Configure directories, not wildcard expressions such as `/var/log/*/*.log`:

```json
"Folders": [
  { "Id": "nginx", "Name": "Nginx", "Folder": "/var/log/nginx" },
  { "Id": "application", "Name": "Application", "Folder": "/var/log/my-app" }
]
```

Place this list inside `Dashboard:Logs`. Each ID must be unique and use lowercase letters, digits, or hyphens. Select the directory in the Logs dropdown. `Dashboard:Logs:Folder` is the single-directory fallback when `Folders` is empty.

Log files must be readable by the service user. Each `<folder>/.serilog-viewer` directory must be writable for its SQLite index. For Nginx, create the index directory without changing ownership of the Nginx log files:

```bash
sudo install -d -o peevi -g "$(id -gn peevi)" -m 700 /var/log/nginx/.serilog-viewer
```

Supported parsers include Serilog text, CLEF/JSON, standard Nginx access/error logs, and the dashboard's own day-first Serilog output. File deletion is disabled. Viewer pages, APIs, and downloads require dashboard authentication; integration routes allow read requests only.

The dashboard logs its own exceptions through Serilog to `LogSettings:FilePath`. Files roll daily, with 30 retained files; Console logging also goes to the systemd journal. Levels are configured under `Serilog:MinimumLevel`. The application's log directory is automatically added to the file viewer when enabled.

The current default is `logs/app-.log`. To use `/var/log/stackwatch` instead, create the directory owned by peevi and set `LogSettings:FilePath` to `/var/log/stackwatch/app-.log` in appsettings. Keep the property name **FilePath**.

For database logs, enable `Dashboard:Logs:Database:Enabled` and supply `Dashboard__Logs__Database__ConnectionString`. Configure its `Schema` and `Table` in appsettings. This connection is separate from PostgreSQL monitoring and must point to an existing Serilog table compatible with the PostgreSQL provider. The dashboard does not create a logging sink for other applications.

If an old parser indexed a Nginx file before the parser update, stop the dashboard and back up that folder's `.serilog-viewer/index.sqlite` and its WAL/SHM companions before removing them from their original location. The viewer rebuilds the index on the next query. Leave source log files untouched.

## .NET processes, Redis, and EC2

**.NET processes:** dotnet-counters discovers and samples other accessible local .NET processes. No application instrumentation is required. Configure `Dashboard:Processes:CountersExecutable`; use an absolute path if the tool is outside the service PATH. `AllowedProcessIds` limits monitoring; an empty list means all accessible processes. CPU, memory, and threads come from OS measurements. Sample collects a bounded System.Runtime counter sample; it is not a permanent collector.

The service user must be able to access the target diagnostic socket. Target and tool need compatible temporary-directory settings, and runtime diagnostics must be enabled. Avoid systemd `PrivateTmp=true` unless diagnostic sockets are deliberately shared. CPU is measured between refreshes; 100% represents one busy core.

**Optional dotnet-monitor:** run it separately and configure `Dashboard:Processes:Monitor:Enabled` and `BaseUrl`, with its API key in `Dashboard__Processes__Monitor__ApiKey`. The dashboard can proxy its `/processes` and `/metrics` endpoints without exposing the key to the browser. Configure the monitor's metrics listener and target selection appropriately. There are no dashboard dump, process-kill, or arbitrary URL operations.

**Redis:** monitoring reads PING and INFO to show clients, memory, operations, cache hits/misses, evictions, and replication status. The bundled client requires `allowAdmin=true` for INFO. This enables the client command; the dashboard exposes no flush, write, key-browsing, or arbitrary command actions. Redis credentials must permit PING and INFO.

**EC2:** set `Dashboard__Aws__Enabled=true`, choose the region, and optionally filter IDs using `Dashboard__Aws__InstanceIds__0`, etc. The AWS SDK uses its normal credential chain, including an IAM role, environment credentials, or AWS_PROFILE. Permissions are `ec2:DescribeInstances` and `cloudwatch:GetMetricStatistics`. The page shows state, CPU, network totals, and failed status checks from CloudWatch. Memory/filesystem metrics require additional agent support. Responses are cached for one minute. NuGet packages are free; AWS services may incur charges.

## Health history and webhooks

[Health history](http://127.0.0.1:5080/integrations/health#/healthchecks) shows PostgreSQL and Redis health checks. The page, assets, and API are behind the same password/MFA authentication gate. PostgreSQL connections have separate named checks. In-memory history resets on restart; SQLite stores accounts, not health history.

`/healthz` is the standard ASP.NET Core health-check route. Its internal poller uses a random process-local `X-Health-Key`; this endpoint is not an anonymous public health feed.

HealthChecks.UI uses absolute asset, API, and webhook paths. Its `UseRelativeResourcesPath`, `UseRelativeApiPath`, and `UseRelativeWebhookPath` options are false to avoid duplicated `/integrations/integrations/` URLs.

Webhooks are optional HTTP notifications for health-state changes. None are configured by default. `/integrations/health-webhooks` is the package's protected webhook route; it is not an account-registration or password-reset endpoint.

## Accounts and recovery

From the solution directory, use the full project path:

```bash
dotnet run --project SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj -- users list
dotnet run --project SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj -- users add alice
dotnet run --project SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj -- users reset-password alice
dotnet run --project SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj -- users reset-mfa alice
dotnet run --project SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj -- users disable alice
dotnet run --project SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj -- users enable alice
dotnet run --project SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj -- users unlock alice
```

For the deployed service, run account commands as peevi from the deployment directory so they use the service's database and keys:

```bash
cd /opt/stackwatch
export DOTNET_ENVIRONMENT=Development
export ASPNETCORE_ENVIRONMENT=Development
dotnet SimpleStackWatch.Dashboard.dll users list
# Example for a new account:
dotnet SimpleStackWatch.Dashboard.dll users add alice
```

Use the same environment and data-directory settings as the service. Passwords are prompted privately; interactive commands need a real terminal. All users have the same dashboard access.

| Command | Effect |
| --- | --- |
| reset-password | Changes password, keeps MFA, invalidates existing sessions |
| reset-mfa | Requires enrollment with a new manual key/code, replaces recovery codes, invalidates sessions |
| disable | Disables login and invalidates sessions |
| enable | Enables an account already enrolled in MFA |
| unlock | Clears login lockout |

A failed or abandoned MFA enrollment rolls back. Successful MFA reset does not enable a disabled account. If a phone is lost, use a single-use recovery code on the verification page. If those codes are also lost, a server operator runs reset-mfa privately with the user present. Server access is the recovery authority; there is no public reset page or admin role.

Account creation and MFA reset hold a SQLite transaction during enrollment, so finish promptly. Login uses Identity lockout, per-IP rate limiting, anti-forgery protection, HttpOnly cookies, and per-request security-stamp validation. Keep server and phone clocks accurate.

## Run permanently with systemd

The example deployment uses these paths:

| Item | Path |
| --- | --- |
| Source solution | /home/peevi/RiderProjects/SimpleStackWatch |
| Debug publish output | /opt/stackwatch |
| Secrets | /etc/stackwatch/systemd.env |
| User database and MFA keys | /opt/stackwatch/data |
| Default application logs | /opt/stackwatch/logs |

Run as peevi. Stop Rider's running instance and the existing service before publishing:

```bash
cd /home/peevi/RiderProjects/SimpleStackWatch
sudo systemctl stop simplestackwatch.service  # If already installed
sudo install -d -o peevi -g "$(id -gn peevi)" -m 755 /opt/stackwatch
dotnet publish SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj \
  -c Debug --no-self-contained -o /opt/stackwatch
install -d -m 700 /opt/stackwatch/data /opt/stackwatch/logs
```

Continue only after publish succeeds. For a first migration with existing accounts, copy the source project's `data` directory, including `keys`, while the app is stopped. These commands assume you are still in the solution directory; skip migration if the deployment already has its current accounts:

```bash
if [ -d SimpleStackWatch.Dashboard/data ] && [ ! -e /opt/stackwatch/data/users.db ]; then
  cp -a SimpleStackWatch.Dashboard/data/. /opt/stackwatch/data/
fi
if [ -d SimpleStackWatch.Dashboard/logs ]; then
  cp -an SimpleStackWatch.Dashboard/logs/. /opt/stackwatch/logs/
fi
```

Keep the source data as a backup; use the deployed CLI afterwards.

Create the secret file:

```bash
sudo install -d -m 700 /etc/stackwatch
sudo touch /etc/stackwatch/systemd.env
sudo chmod 600 /etc/stackwatch/systemd.env
sudo nano /etc/stackwatch/systemd.env
```

Paste the environment settings from the secrets section with your real values. Keep Urls, AllowedHosts, DataDirectory, and LogSettings:FilePath in appsettings, not duplicated in this environment file. Preserve any other enabled integration credentials. Do not commit environment files, user databases, or key material.

Create the service using the installed dotnet path:

```bash
stackwatch_dotnet="$(command -v dotnet)"
sudo tee /etc/systemd/system/simplestackwatch.service > /dev/null <<EOF_SERVICE
[Unit]
Description=SimpleStackWatch Dashboard (Debug)
Wants=network-online.target
After=network-online.target

[Service]
Type=simple
User=peevi
WorkingDirectory=/opt/stackwatch
ExecStart="$stackwatch_dotnet" /opt/stackwatch/SimpleStackWatch.Dashboard.dll
Environment=DOTNET_ENVIRONMENT=Development
Environment=ASPNETCORE_ENVIRONMENT=Development
Environment="PATH=/home/peevi/.dotnet/tools:/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin"
EnvironmentFile=/etc/stackwatch/systemd.env
Restart=on-failure
RestartSec=5
TimeoutStopSec=30
UMask=0077
NoNewPrivileges=true
StandardOutput=journal
StandardError=journal

[Install]
WantedBy=multi-user.target
EOF_SERVICE

sudo systemd-analyze verify /etc/systemd/system/simplestackwatch.service
sudo systemctl daemon-reload
sudo systemctl enable --now simplestackwatch.service
sudo systemctl status simplestackwatch.service --no-pager
```

The app starts at boot and restarts after failures. Closing the terminal does not stop it. It runs the published Debug DLL, not dotnet watch; DOTNET_WATCH variables have no effect. The older `scripts/monitor-dashboard.service` example uses different deployment paths; use this service for the setup above.

## Nginx HTTPS and hosts

On the computer opening the dashboard, add this to `/etc/hosts` when Nginx runs on that same computer:

```text
127.0.0.1 stackwatch.lumestack.local
```

Use the Nginx machine's IP for access from another computer. The certificate must cover stackwatch.lumestack.local or *.lumestack.local and be trusted by the browser.

On Ubuntu, create `/etc/nginx/sites-available/stackwatch`:

```nginx
server {
    listen 443 ssl;
    server_name stackwatch.lumestack.local;

    ssl_certificate     /home/peevi/.aspnet/https/lumestack/lumestack.local.pem;
    ssl_certificate_key /home/peevi/.aspnet/https/lumestack/lumestack.local-key.pem;

    location / {
        proxy_pass http://127.0.0.1:5080;
        proxy_http_version 1.1;
        proxy_set_header Host $host;
        proxy_set_header X-Forwarded-Host $host;
        proxy_set_header X-Forwarded-Proto $scheme;
        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
        proxy_set_header Upgrade $http_upgrade;
        proxy_set_header Connection "upgrade";
    }
}
```

If not already enabled, enable the site, validate, and reload:

```bash
sudo ln -s /etc/nginx/sites-available/stackwatch /etc/nginx/sites-enabled/stackwatch
sudo nginx -t && sudo systemctl reload nginx
```

Use one server block for this hostname; do not enable a second copy of an existing configuration. Open [https://stackwatch.lumestack.local](https://stackwatch.lumestack.local).

Program.cs already processes X-Forwarded-For and X-Forwarded-Proto before HSTS, cookies, and rate limiting, trusting only 127.0.0.1 and ::1 for one proxy hop. Nginx preserves Host. A proxy on another machine or in a container needs its actual trusted peer address configured.

## Helpful aliases

These aliases replace previous definitions when the file is sourced after them:

```bash
cat > ~/peevi-stackwatch-aliases.sh <<'EOF_ALIASES'
alias peevi-stackwatch-start='sudo systemctl start simplestackwatch.service'
alias peevi-stackwatch-stop='sudo systemctl stop simplestackwatch.service'
alias peevi-stackwatch-restart='sudo systemctl restart simplestackwatch.service'
alias peevi-stackwatch-status='sudo systemctl status simplestackwatch.service --no-pager'
alias peevi-stackwatch-logs='sudo journalctl -u simplestackwatch.service -f'
alias peevi-stackwatch-errors='sudo journalctl -u simplestackwatch.service -n 100 --no-pager'
alias peevi-stackwatch-env='sudo nano /etc/stackwatch/systemd.env'
alias peevi-stackwatch-build='(cd /home/peevi/RiderProjects/SimpleStackWatch && sudo systemctl stop simplestackwatch.service && dotnet publish SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj -c Debug --no-self-contained -o /opt/stackwatch && sudo systemctl start simplestackwatch.service)'
alias peevi-stackwatch-rebuild='peevi-stackwatch-build'
EOF_ALIASES

grep -qxF 'source "$HOME/peevi-stackwatch-aliases.sh"' ~/.bashrc || \
  echo 'source "$HOME/peevi-stackwatch-aliases.sh"' >> ~/.bashrc
source ~/.bashrc
```

After code or source-appsettings changes, use `peevi-stackwatch-rebuild`. It stops the service, publishes Debug, and starts only if publish succeeds. A failed publish leaves the service stopped. After editing secrets, use `peevi-stackwatch-restart`. Changes made directly to published appsettings may be overwritten by the next publish; edit the source configuration.

## Troubleshooting and checks

- **Redis 503:** check the environment string includes allowAdmin=true. Test `redis-cli -h 127.0.0.1 -p 6379 PING` and `INFO`. Monitoring errors intentionally log only exception types to avoid leaking connection secrets.
- **Blank health UI:** check requests use `/integrations/health-resources/`, not `/integrations/integrations/health-resources/`; keep the three relative-path flags false and hard-refresh.
- **No Nginx logs:** check file read permissions, index-directory write permissions, parser format, and active date filters.
- **Service fails:** use `peevi-stackwatch-errors`; check publish success, runtime path, required environment file, and whether another app occupies port 5080. After fixing repeated startup failures, run `sudo systemctl reset-failed simplestackwatch.service` and start it again.
- **Unexpected settings:** check environment-specific JSON and environment overrides. Service launchSettings values are not loaded.

From the dashboard project directory:

```bash
npm test
npm run check
```

If the sibling test project is present in the full solution, run from the solution root:

```bash
dotnet test SimpleStackWatchDashboard.Test/SimpleStackWatchDashboard.Test.csproj
```

Previous authoring checks passed JavaScript tests and syntax checks. Backend compilation and C# integration tests could not run in the authoring environment because CoreCLR could not start (HRESULT 0x8007000E). Run the checks on your .NET 10 host. This project archive does not include the sibling C# test project.

Package references are pinned. Keep Microsoft.EntityFrameworkCore.InMemory aligned with the other EF Core packages; mismatches can prevent the HealthChecks.UI service from starting. The file viewer includes frontend routing and embedded-asset fixes for the pinned Serilog.Viewer version, plus the Nginx parser.

## References

- [Serilog.Viewer](https://www.nuget.org/packages/Serilog.Viewer/)
- [Serilog.UI](https://github.com/serilog-contrib/serilog-ui)
- [ASP.NET Core health checks](https://github.com/Xabaril/AspNetCore.Diagnostics.HealthChecks)
- [dotnet-counters](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/dotnet-counters)
- [dotnet-monitor](https://github.com/dotnet/dotnet-monitor)
- [ASP.NET Core with Nginx](https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/linux-nginx?view=aspnetcore-10.0)
- [AWS SDK for .NET](https://docs.aws.amazon.com/sdk-for-net/v4/developer-guide/)

Bootstrap is bundled locally; there is no frontend build step or runtime CDN dependency. See `wwwroot/vendor/bootstrap.LICENSE` and the bundled Serilog.Viewer notice for their licenses. Referenced NuGet packages retain their respective licenses.
