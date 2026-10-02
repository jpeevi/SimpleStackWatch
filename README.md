# SimpleStackWatch

Monitor application logs, local .NET processes, PostgreSQL, Redis, and EC2.

Intended for development and UAT applications running as a single instance. Kubernetes support is coming soon.

## Getting started

Requires the .NET 10 SDK. From the solution directory:

```bash
dotnet restore SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj
dotnet build SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj -c Debug
dotnet tool install --global dotnet-counters --version 10.0.731102
dotnet run --project SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj -- users add alice
dotnet run --project SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj
```

Skip tool installation if dotnet-counters is already installed. Configure accessible log directories before starting, or disable the file viewer. Open [http://127.0.0.1:5080](http://127.0.0.1:5080).

## Configuration

Settings load from `appsettings.json`, then `appsettings.{Environment}.json`. Environment variables override JSON. Restart after changes.

Merge these values into your existing settings:

```json
{
  "Urls": "http://127.0.0.1:5080",
  "AllowedHosts": "localhost;127.0.0.1",
  "Dashboard": {
    "DataDirectory": "data",
    "Logs": {
      "Folders": [
        { "Id": "application", "Name": "Application", "Folder": "logs" }
      ]
    },
    "Processes": { "CountersExecutable": "dotnet-counters" },
    "Postgres": { "Databases": [{ "Name": "Application" }] }
  },
  "LogSettings": { "FilePath": "logs/app-.log" }
}
```

Relative paths resolve from the application root. Back up the entire `Dashboard:DataDirectory`, including the database and keys. Keep credentials and account data out of Git.

Supply connection strings through environment variables. For a service environment file, use `KEY=value` syntax:

```dotenv
Dashboard__Postgres__Databases__0__ConnectionString="Host=127.0.0.1;Port=5432;Database=application;Username=monitor;Password=YOUR_PASSWORD;Timeout=5;Command Timeout=5"
Dashboard__Redis__ConnectionString="127.0.0.1:6379,allowAdmin=true,connectTimeout=5000,asyncTimeout=5000"
Dashboard__Aws__Enabled=false
Dashboard__Aws__Region=YOUR_REGION
```

Replace example values. Add `password=YOUR_PASSWORD` for authenticated Redis. For additional PostgreSQL databases, append names to the JSON list and supply connection strings at indexes `1`, `2`, etc. Names and indexed variables must use the same order.

For local development, use shell variables or launchSettings.json. systemd does not read launchSettings; configure its EnvironmentFile separately.

## Monitoring

- **Logs:** use directory paths, not globs. Files must be readable and each directory's `.serilog-viewer` index must be writable by the application user. Supports Serilog, CLEF/JSON, and standard Nginx logs. The dashboard's own log directory is included automatically. Database logs require a compatible Serilog table and `Dashboard__Logs__Database__ConnectionString`.
- **Processes:** dotnet-counters must be available to the application user, with access to the target diagnostic sockets. Configure an absolute executable path when needed. `AllowedProcessIds` restricts monitoring; an empty list includes all accessible processes. Optional dotnet-monitor runs separately.
- **PostgreSQL:** the database selector controls sessions and health checks. Query text is disabled by default. Grant the monitoring account appropriate permissions to view other users' sessions.
- **Redis:** monitoring reads PING and INFO. Include `allowAdmin=true` for the bundled client.
- **EC2:** enable AWS and provide credentials through the SDK credential chain. Required permissions: `ec2:DescribeInstances` and `cloudwatch:GetMetricStatistics`. AWS services may incur charges.

Health history at `/integrations/health` requires login and resets on restart. `/healthz` is protected by the internal poller's private key. Webhooks are optional HTTP notifications for health changes; none are configured by default.

## User setup

From the solution directory:

```bash
dotnet run --project SimpleStackWatch.Dashboard/SimpleStackWatch.Dashboard.csproj -- users add alice
```

1. Enter a password when prompted.
2. In Google Authenticator, choose **Enter a setup key → Time based** and enter the displayed key.
3. Enter the current authenticator code to finish setup.
4. Save the recovery codes privately.

Sign in with your username, password, and authenticator code.

For a published application, run account commands from its deployment directory as the application user:

```bash
export DOTNET_ENVIRONMENT=Development ASPNETCORE_ENVIRONMENT=Development
dotnet SimpleStackWatch.Dashboard.dll users add alice
dotnet SimpleStackWatch.Dashboard.dll users list
dotnet SimpleStackWatch.Dashboard.dll users reset-password alice
dotnet SimpleStackWatch.Dashboard.dll users reset-mfa alice
dotnet SimpleStackWatch.Dashboard.dll users disable alice
dotnet SimpleStackWatch.Dashboard.dll users enable alice
dotnet SimpleStackWatch.Dashboard.dll users unlock alice
```

Use the service's environment and data-directory settings; its environment file is not automatically loaded into your terminal. Set both environment values to Test or Uat when appropriate.

Password reset keeps MFA. MFA reset verifies a new setup key and replaces recovery codes. Successful resets invalidate existing sessions. Disable blocks login, enable requires MFA enrollment, and unlock clears lockout.

If the authenticator is lost, use a recovery code or ask a server operator to run `users reset-mfa`.

## Test environment

Create `appsettings.Test.json` with separate account data and logs:

```json
{
  "Dashboard": {
    "DataDirectory": "data-test",
    "Postgres": { "Databases": [{ "Name": "Test" }] }
  },
  "LogSettings": { "FilePath": "logs/test/app-.log" }
}
```

Set `DOTNET_ENVIRONMENT` and `ASPNETCORE_ENVIRONMENT` to Test, supply the test database connection, and create accounts using that environment. The default port is shared with Development, so run one at a time.

## Reverse proxy

Keep the app listening on loopback. Add your public hostname to `AllowedHosts` and configure Nginx to forward requests to `http://127.0.0.1:5080`:

```nginx
proxy_set_header Host $host;
proxy_set_header X-Forwarded-Proto $scheme;
proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
```

Use a trusted HTTPS certificate. For a local hostname, add its IP mapping to `/etc/hosts`. The application trusts forwarded headers from a loopback proxy; configure trusted peer addresses separately for a remote or containerized proxy.

## Checks

From the dashboard directory:

```bash
npm test
npm run check
```

From the solution directory, with the test project available:

```bash
dotnet test SimpleStackWatchDashboard.Test/SimpleStackWatchDashboard.Test.csproj
```
