# AGENTS.md — SimpleStackWatch

Instructions for coding agents and contributors working in this repository. Apply these rules to changes throughout the repository unless a more specific maintainer-approved instruction applies. The goal is a small, generic, secure monitoring application that is easy to operate and maintain.

## 1. Workflow and approval

Use **Inspect → Plan → Approve → Implement → Verify → Deliver**.

- Inspect the latest relevant source, project files, configuration and tests. Do not work from stale snippets or overwrite newer implementations. Preserve existing local changes.
- Reading, investigation, planning and review do not authorize file modifications. Before creating, modifying, deleting or refactoring code, scripts, configuration, dependencies or documentation, present Goal, Approach, Files, 3–5 implementation steps and Verification. Ask `Shall I proceed?` and wait for explicit approval. Complete the approved scope without repeatedly asking.
- Stop and obtain separate approval for significant changes to architecture, framework, dependencies, storage, public contracts, authentication, deployment or core behavior. Explain reason, impact, risks and alternatives. Do not expand scope silently.
- Make the smallest coherent change that solves the actual problem. Avoid incidental formatting, speculative abstractions, unrelated package upgrades and unnecessary restructuring.
- Report actual test results and limitations. Never invent successful builds, tests, runtime behavior or production readiness.

## 2. Technology and repository layout

The current solution targets `net11.0`. Linux, Bash and the .NET CLI are the expected development workflow. Preserve the existing ASP.NET Core MVC/Razor and JavaScript ES module architecture, dependency injection, EF Core/SQLite identity storage and SQLite monitoring store. Do not introduce a frontend framework, new database or monitoring platform without approval.

| Path | Responsibility |
| --- | --- |
| `SimpleStackWatch.slnx` | Solution definition. |
| `SimpleStackWatch.Dashboard/Program.cs` | Configuration, service registration, authentication, middleware and routes. |
| `SimpleStackWatch.Dashboard/Auth/` | ASP.NET Core Identity model/storage and user CLI. |
| `SimpleStackWatch.Dashboard/Models/` | Dashboard options, private configuration mapping, view models. |
| `SimpleStackWatch.Dashboard/Controllers/` | MVC pages and authenticated browser API. |
| `SimpleStackWatch.Dashboard/Services/` | Monitoring poller, state, history, logs, process, database and AWS integrations. |
| `SimpleStackWatch.Dashboard/Mcp/` | MCP tools and bearer-token verification. |
| `SimpleStackWatch.Dashboard/Views/` | Razor UI. |
| `SimpleStackWatch.Dashboard/wwwroot/` | Browser scripts, CSS and vendor assets. |
| `SimpleStackWatch.Dashboard/tests/` | Frontend tests. |
| `SimpleStackWatchDashboard.Test/` | xUnit/unit and ASP.NET Core integration tests. |
| `SimpleStackWatch.Dashboard/scripts/` | Example systemd unit. |
| `run-debug.sh`, `run-tests.sh`, `run-cli.sh` | Root-level Linux workflows. |

The dashboard's existing C# root namespace is `MonitorDashboard`; tests use `MonitorDashboard.Tests`. Preserve existing namespaces, target frameworks, package versions, directory structure and conventions. Inspect both `.csproj` files and solution definition before changing build behavior. Do not assume a `global.json` or SDK pin exists if it is absent.

## 3. Generic public repository

SimpleStackWatch must not know about any particular deployment, organization or business application. Names, hosts, target URLs, credentials, instance IDs, service names and data paths are operator configuration, not application constants. Use neutral sample names, reserved example domains and synthetic data in tests/docs. Never introduce deployment-specific integrations or defaults merely because one operator uses them.

### Private configuration boundary

- Harmless tuning settings belong in generic appsettings files. Private settings are rebuilt from process environment variables by `PrivateDashboardConfiguration` and explicit environment readers in the integration services/`Program.cs`.
- Do not weaken this boundary by binding private values from committed JSON or CLI arguments. Local `Properties/launchSettings.json` works because the launch profile injects process environment variables; ordinary appsettings JSON does not.
- Keep private profiles, `.env.local`, `testconfig.json`, deployment environment files, logs, credentials, database files, keys and backups untracked. Maintain `.gitignore`. Public `.env.example` and JSON examples must remain generic. Never force-add private files or expose secret contents in build/test logs or output archives.
- Keep collection indexes and paired values consistent. PostgreSQL currently uses sequential indexes for name/connection-string binding and history. HTTP headers must remain associated with the original configuration index, including when HTTP indexes have gaps.
- Update relevant options, environment-only mappings, validation, tests, examples and README together when configuration changes. Retain backward compatibility, including the legacy PostgreSQL connection variable and explicit HTTP IDs.
- Report a suspected exposed secret without repeating it; recommend revocation/rotation and history review. Do not rewrite public history or rotate real credentials without explicit authorization.

## 4. Monitoring architecture and contracts

`MonitoringService` is the hosted poller and target registry. `MonitoringStatus` defines health transitions; `MonitoringStore` persists history and state in a separate SQLite database. The unified dashboard and MCP consume the same monitoring state. Detailed integration services are separate. Reuse these components rather than creating a second poller or per-page background jobs.

- Preserve existing target kinds, status meanings (`Healthy`, `Degraded`, `Down`, `Unknown`), thresholds, stale-reading detection, timestamps, latency, retention and transition-based alert behavior.
- Keep target IDs stable. Automatic HTTP IDs derive from name, environment and canonical URL rather than index; explicit IDs remain supported. Reject ambiguous identities. Do not expose raw private URLs or credentials in generated IDs or diagnostics.
- Keep HTTP monitoring read-only, with bounded concurrency, cancellation and per-check timeouts. Preserve exact expected-status mode and opt-in reachability mode (HTTP below 500 counts as reachable). Do not silently follow redirects, weaken TLS checks or send credentials to alternate hosts. Do not present S3 endpoint reachability as bucket health.
- Preserve the configurable HTTP target limit, input validation and generic labels. Do not replace operator configuration with automatic network discovery or accept untrusted public target URLs.
- Keep local Linux checks local. Do not add remote command execution, cloud resource mutation, database writes or messaging actions to monitoring. Use least privilege and existing safe APIs. Avoid shell interpolation of configured service names or paths.
- Preserve polling/history behavior across failures and restarts. Do not mark missing or stale measurements healthy. Avoid unbounded queues or history; treat optional webhook delivery as best-effort.
- Use cancellation and dispose HTTP responses, database connections and other resources correctly. Avoid raw provider exceptions or response bodies in exposed monitoring results.

## 5. Security and access control

Preserve authentication, rate limiting, antiforgery validation, MFA, password length, account lockout, security-stamp checks and private storage. Never introduce default shared accounts, bypasses, plaintext credential storage or a public registration endpoint. Do not change CLI password/MFA prompts to command-line secrets.

- Browser routes and `/api/*` dashboard endpoints use existing authenticated sessions. Integration middleware must remain behind the authentication gate; do not assume MVC authorization protects middleware-based viewers.
- MCP is disabled by default, separately protected by a bearer token at `/api/mcp`. A valid browser cookie does not authorize MCP. Preserve constant-time token verification, token validation at startup, and disabled/unauthorized behavior. Do not broaden access or add write tools without separate approval.
- `/healthz` uses the internal process-local health credential; do not expose it anonymously as a shortcut. Preserve the existing private health integration.
- Retain host filtering, trusted proxy boundaries and secure deployment recommendations. Nginx IP allowlisting for MCP must not inadvertently block dashboard APIs. Do not trust arbitrary forwarded headers or remove TLS validation.
- Protect account and monitoring databases, Data Protection keys, logs and backup files. Prefer safe projections over serializing options, exception details or provider objects. No credentials in JSON responses, MCP outputs, frontend markup, tests or logs.
- Audit dependencies and investigate relevant security advisories. Do not suppress NU1605 or vulnerability warnings simply to make a build succeed. Major package changes need explicit approval and compatible integration verification.

## 6. UI and frontend conventions

Maintain the monochrome design, system monospace font stack and light/dark themes. Use text, icons and contrast rather than color alone to distinguish status. Keep navigation simple, with Overview, Logs, .NET processes, Endpoints, PostgreSQL, Redis and EC2 at the same sidebar level. Preserve the compact sign-in experience and responsive desktop/mobile navigation.

- Reuse existing Razor views, Bootstrap assets, CSS and ES modules; add no new framework or external font by default.
- Keep Endpoints HTTP-only (including S3 reachability); reuse Overview API/history. Preserve default unhealthy-first ordering, sortable name/status/latency/last-checked columns, filters, search and history. Do not remove endpoints from Overview or change MCP data when making UI-only changes.
- Preserve accessible names, keyboard support, focus visibility, `aria-sort` where appropriate, readable dark-mode contrast and narrow-screen layouts. Third-party embedded viewers may have their own styling.
- Render configuration/provider values as text, not unsanitized HTML. Do not expose private connection strings, URL secrets or auth headers to client code.
- Keep existing routes, DOM contracts and API response shapes unless a contract change is specifically approved. Add focused frontend tests for sorting/filtering, refresh, navigation, errors and history as relevant.

## 7. MCP and API compatibility

The public-facing MCP URL pattern is `/api/mcp` (stateless Streamable HTTP). Preserve it for operator reverse-proxy rules. The current four tools are `get_overview`, `list_targets`, `get_target_status(targetId)` and `get_target_history(targetId, limit)`. They expose read-only monitoring metadata/history from the existing poller/store; the history limit is clamped to 1–200. Do not turn them into arbitrary HTTP, command, log or database query proxies.

The Endpoints and Overview pages use `GET /api/overview` and `GET /api/overview/history`; the other dashboard controllers and authenticated diagnostic APIs remain intact. Maintain compatibility with existing route names, serialization, cancellation, authorization and error contracts. If a change touches MCP, verify initialization, tool discovery, a tool call, unknown target handling, disabled mode and invalid/missing token rejection using the existing integration-test approach.

## 8. Verification and tests

Prefer the existing scripts from the solution root:

```bash
./run-tests.sh
```

Run focused checks as appropriate:

```bash
dotnet restore SimpleStackWatch.slnx
dotnet build SimpleStackWatch.slnx -c Release --no-restore
dotnet test SimpleStackWatch.slnx -c Release --no-build
(cd SimpleStackWatch.Dashboard && npm test && npm run check)
bash -n run-debug.sh run-tests.sh run-cli.sh
```

For dependency changes, also audit direct/transitive packages and verify restore compatibility. For UI changes, check both themes, desktop/mobile viewports, keyboard navigation and failure/empty/loading states with safe simulated data. For scripts, test command routing, nonzero exit handling and actual invocation where possible; mock CLI checks do not prove real .NET execution.

- Add targeted regression tests for meaningful behavior changes. Use the existing xUnit test project and frontend Node test suite. Avoid new test frameworks without approval.
- Prefer temporary isolated data directories, deterministic fake HTTP responses, synthetic data and fixture-generated credentials. Do not contact live systems by default or use real production data.
- `testconfig.json` is optional and untracked; participating fixtures translate its supported values into temporary process environment variables, respect preexisting environment values and restore them on disposal. Preserve isolation and avoid parallel tests mutating shared process environment unsafely. Empty arrays do not clear inherited values. Production must never read this file.
- Do not claim an unavailable check passed. Report executed commands, results and blockers (missing SDK, network access, external service, browser, etc.). Static review, XML/JSON validation and ZIP integrity are not substitutes for compilation and runtime tests.

## 9. Scripts and deployment

Keep Bash scripts Linux-compatible and resolve paths relative to the repository rather than relying on the caller's directory. Preserve fail-fast behavior and meaningful exit codes.

- `run-debug.sh`: full clean, restore, non-incremental Debug build, then `dotnet watch` using `SimpleStackWatch (Local HTTP)`. Do not restore `--no-launch-profile` accidentally. Optional `.env.local` is trusted local Bash only; never source untrusted files.
- `run-cli.sh`: interactive menu/direct commands/help; defaults to Development, preserves prompts and uses matching account storage. It deliberately does not load launch profiles or systemd environment files.
- `run-tests.sh`: runs Release .NET tests, frontend tests and JavaScript syntax checks; reports failure if any fail.
- Preserve the single-instance deployment architecture, non-root service user, mandatory private systemd environment file, loopback listener and writable private storage. Check the published DLL name against `ExecStart` when touching deployment; the supplied unit has an older name that must be reconciled before use. Do not modify server settings or perform deployments without authorization.

## 10. Documentation, review and delivery

Keep README accurate, easy to navigate and suitable for a public audience. Document prerequisites, setup, configuration defaults, routes, monitoring limitations and security boundaries. Examples must be copyable with obvious placeholders. Do not invent a license or unsupported features.

Production reviews focus on significant security vulnerabilities, correctness failures, reliability risks, material performance problems and compatibility failures. Distinguish confirmed defects from optional suggestions. Avoid blocking approval on stylistic preferences. If no critical issue is identified but verification is incomplete, state that production readiness is pending verification.

When delivering a change archive, include only added/modified files under one `SimpleStackWatch/` top-level folder with original project-relative paths. Exclude private profiles, local test configuration, credentials, build output, caches, logs and unrelated files from public/general-purpose delivery. Identify deletions separately with precise paths and approved removal instructions. Verify archive contents, integrity and extraction locations. Never claim an archive exists without creating and checking it.

### Definition of done

- Approved scope implemented with current architecture and compatibility preserved.
- Relevant tests and checks executed, with failures and unavailable checks documented.
- Private configuration boundaries, auth, generic examples and data isolation preserved.
- Documentation updated for changed behavior, with no unrelated changes or secrets.
- Changed files listed; delivery verified if requested.

Final report: **Status** (Completed / Partial / Blocked), **Changes**, **Files** (Added / Modified / Removed), **Verification**, **Delivery** (when applicable), and **Notes** (material limitations only). Be concise and make the next action clear.
