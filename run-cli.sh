#!/usr/bin/env bash
set -Eeuo pipefail

ROOT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
DASHBOARD_DIR="$ROOT_DIR/SimpleStackWatch.Dashboard"
PROJECT="$DASHBOARD_DIR/SimpleStackWatch.Dashboard.csproj"

show_help() {
  cat <<'HELP'
SimpleStackWatch user-management CLI

Usage:
  ./run-cli.sh                      Open the interactive menu
  ./run-cli.sh menu                 Open the interactive menu
  ./run-cli.sh list                 List users
  ./run-cli.sh add [username]       Add a user (prompts for password and MFA)
  ./run-cli.sh reset-password USER  Reset a user's password
  ./run-cli.sh reset-mfa USER       Reset a user's authenticator
  ./run-cli.sh enable USER          Enable a user
  ./run-cli.sh disable USER         Disable a user
  ./run-cli.sh unlock USER          Unlock a user
  ./run-cli.sh help | --help | -h   Show this help

Examples:
  ./run-cli.sh add testuser
  ./run-cli.sh list
  ./run-cli.sh reset-password testuser
  ./run-cli.sh --help

Requirements:
  - .NET 11 SDK and a complete SimpleStackWatch checkout
  - An interactive terminal for password and MFA operations

Configuration:
  Defaults to Development when no environment is set. Set both
  DOTNET_ENVIRONMENT and ASPNETCORE_ENVIRONMENT to select another
  environment. Set Dashboard__DataDirectory to use the SAME account
  database as the running dashboard. Launch profiles and systemd
  environment files are not loaded by this script.
  Do not pass passwords or authenticator secrets as arguments.
HELP
}

usage_error() {
  printf 'ERROR: %s\n\n' "$1" >&2
  show_help >&2
  return 2
}

run_user_command() {
  if [[ ! -f "$PROJECT" ]]; then
    printf 'ERROR: Dashboard project not found: %s\n' "$PROJECT" >&2
    return 1
  fi

  if ! command -v dotnet >/dev/null 2>&1; then
    printf 'ERROR: dotnet was not found. Install the .NET 11 SDK.\n' >&2
    return 127
  fi

  if ! dotnet --list-sdks | grep -Eq '^11\.'; then
    printf 'ERROR: .NET 11 SDK is required. Installed SDKs:\n' >&2
    dotnet --list-sdks >&2
    return 1
  fi

  if [[ -n "${DOTNET_ENVIRONMENT:-}" && -n "${ASPNETCORE_ENVIRONMENT:-}" &&
        "$DOTNET_ENVIRONMENT" != "$ASPNETCORE_ENVIRONMENT" ]]; then
    printf 'ERROR: DOTNET_ENVIRONMENT and ASPNETCORE_ENVIRONMENT must match.\n' >&2
    return 2
  fi

  local target_environment="${ASPNETCORE_ENVIRONMENT:-${DOTNET_ENVIRONMENT:-Development}}"
  export DOTNET_ENVIRONMENT="$target_environment"
  export ASPNETCORE_ENVIRONMENT="$target_environment"

  # Run from the dashboard directory so relative data/log paths match run-debug.sh.
  (cd "$DASHBOARD_DIR" && dotnet run --project "$PROJECT" \
    --configuration Debug --no-launch-profile -- users "$@")
}

execute_command() {
  local command="${1:-}"
  case "$command" in
    list)
      [[ $# -eq 1 ]] || { usage_error 'list takes no arguments.'; return 2; }
      ;;
    add)
      [[ $# -le 2 ]] || { usage_error 'add accepts at most one username.'; return 2; }
      ;;
    reset-password|reset-mfa|enable|disable|unlock)
      [[ $# -eq 2 && -n "$2" ]] || { usage_error "$command requires a username."; return 2; }
      ;;
    *)
      usage_error "Unknown command: $command"
      return 2
      ;;
  esac
  run_user_command "$@"
}

prompt_for_username() {
  local description="$1"
  printf 'Username for %s: ' "$description" >&2
  IFS= read -r INPUT_USERNAME || return 1
  if [[ -z "$INPUT_USERNAME" ]]; then
    printf 'ERROR: Username cannot be blank.\n' >&2
    return 1
  fi
}

run_menu_action() {
  local action="$1"
  if [[ "$action" == add ]]; then
    printf 'Username (blank to enter in the CLI): ' >&2
    local username
    IFS= read -r username || return 1
    if [[ -n "$username" ]]; then
      execute_command add "$username"
    else
      execute_command add
    fi
  elif [[ "$action" == list ]]; then
    execute_command list
  else
    local INPUT_USERNAME=''
    prompt_for_username "$action" || return 1
    execute_command "$action" "$INPUT_USERNAME"
  fi
}

show_menu() {
  if [[ ! -t 0 ]]; then
    printf 'ERROR: The interactive menu requires a terminal. Use --help for direct commands.\n' >&2
    return 2
  fi
  local choice action
  while true; do
    cat <<'MENU'

SimpleStackWatch - User Management
  1) List users          5) Enable user
  2) Add user            6) Disable user
  3) Reset password      7) Unlock user
  4) Reset MFA           8) Help
  0) Exit
MENU
    printf 'Choice: '
    IFS= read -r choice || { printf '\n'; return 0; }
    case "$choice" in
      0) return 0 ;;
      8) show_help; continue ;;
      1) action=list ;;
      2) action=add ;;
      3) action=reset-password ;;
      4) action=reset-mfa ;;
      5) action=enable ;;
      6) action=disable ;;
      7) action=unlock ;;
      *) printf 'Invalid choice. Select 0-8.\n' >&2; continue ;;
    esac
    if run_menu_action "$action"; then
      printf '\nCommand completed.\n'
    else
      printf '\nCommand failed. Review the error above.\n' >&2
    fi
  done
}

case "${1:-}" in
  ''|menu)
    [[ $# -le 1 ]] || { usage_error 'menu takes no arguments.'; exit 2; }
    show_menu
    ;;
  help|--help|-h)
    [[ $# -eq 1 ]] || { usage_error 'help takes no arguments.'; exit 2; }
    show_help
    ;;
  *) execute_command "$@" ;;
esac
