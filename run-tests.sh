#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
SOLUTION="$ROOT_DIR/SimpleStackWatch.slnx"
DASHBOARD_DIR="$ROOT_DIR/SimpleStackWatch.Dashboard"

if [[ ! -f "$SOLUTION" || ! -f "$DASHBOARD_DIR/package.json" ]]; then
  printf 'ERROR: Run this script from a complete SimpleStackWatch checkout.\n' >&2
  exit 1
fi

for executable in dotnet node npm; do
  if ! command -v "$executable" >/dev/null 2>&1; then
    printf 'ERROR: Required executable not found: %s\n' "$executable" >&2
    exit 127
  fi
done

if ! dotnet --list-sdks | grep -Eq '^11\.'; then
  printf 'ERROR: .NET 11 SDK not found. Installed SDKs:\n' >&2
  dotnet --list-sdks >&2
  exit 1
fi

failed=0

printf '\n=== .NET tests (Release) ===\n'
if dotnet test "$SOLUTION" --configuration Release; then
  dotnet_result=PASS
else
  dotnet_result=FAIL
  failed=1
fi

printf '\n=== Frontend tests ===\n'
if (cd "$DASHBOARD_DIR" && npm test); then
  frontend_result=PASS
else
  frontend_result=FAIL
  failed=1
fi

printf '\n=== JavaScript syntax checks ===\n'
if (cd "$DASHBOARD_DIR" && npm run check); then
  syntax_result=PASS
else
  syntax_result=FAIL
  failed=1
fi

printf '\n=== Summary ===\n.NET tests: %s\nFrontend tests: %s\nJS syntax checks: %s\n' \
  "$dotnet_result" "$frontend_result" "$syntax_result"
exit "$failed"
