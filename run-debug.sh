#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
DASHBOARD_DIR="$ROOT_DIR/SimpleStackWatch.Dashboard"
PROJECT="$DASHBOARD_DIR/SimpleStackWatch.Dashboard.csproj"
SOLUTION="$ROOT_DIR/SimpleStackWatch.slnx"

# Optional private local configuration (Bash syntax). Never commit this file.
LOCAL_ENV_FILE="$ROOT_DIR/.env.local"
if [[ -e "$LOCAL_ENV_FILE" ]]; then
  if [[ ! -f "$LOCAL_ENV_FILE" || ! -r "$LOCAL_ENV_FILE" ]]; then
    printf 'ERROR: Cannot read local configuration: %s\n' "$LOCAL_ENV_FILE" >&2
    exit 1
  fi
  # shellcheck source=/dev/null
  source "$LOCAL_ENV_FILE"
fi

if [[ ! -f "$PROJECT" ]]; then
  printf 'ERROR: Dashboard project not found: %s\n' "$PROJECT" >&2
  exit 1
fi

if [[ ! -f "$SOLUTION" ]]; then
  printf 'ERROR: Solution not found: %s\n' "$SOLUTION" >&2
  exit 1
fi

if ! command -v dotnet >/dev/null 2>&1; then
  printf 'ERROR: dotnet is not installed or is not on PATH. Install the .NET 11 SDK.\n' >&2
  exit 127
fi

if ! dotnet --list-sdks | grep -Eq '^11\.'; then
  printf 'ERROR: .NET 11 SDK not found. Installed SDKs:\n' >&2
  dotnet --list-sdks >&2
  exit 1
fi

export DOTNET_ENVIRONMENT=Development
export ASPNETCORE_ENVIRONMENT=Development
export DOTNET_WATCH_RESTART_ON_RUDE_EDIT=true
export DOTNET_WATCH_SUPPRESS_LAUNCH_BROWSER=1

cd "$ROOT_DIR"
printf 'Cleaning SimpleStackWatch solution (Debug)...\n'
dotnet clean "$SOLUTION" --configuration Debug

printf '\nRestoring SimpleStackWatch solution...\n'
dotnet restore "$SOLUTION"

printf '\nRebuilding full SimpleStackWatch solution (Debug, non-incremental)...\n'
dotnet build "$SOLUTION" --configuration Debug --no-restore --no-incremental

cd "$DASHBOARD_DIR"
printf '\nStarting dashboard in Development mode with file watching. Press Ctrl+C to stop.\n'
exec dotnet watch --project "$PROJECT" run --configuration Debug --launch-profile "SimpleStackWatch (Local HTTP)" --no-restore
