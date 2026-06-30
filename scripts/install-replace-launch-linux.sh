#!/usr/bin/env bash
set -euo pipefail

CONFIGURATION="Release"
INSTALL_DIR="/opt/v2rayN"
LAUNCHER="/usr/bin/v2rayn"
RID=""
DO_BACKUP=0
DO_BUILD=1
DO_START=1

die() {
  echo "error: $*" >&2
  exit 1
}

log() {
  echo "==> $*"
}

usage() {
  cat <<'USAGE'
Usage: bash scripts/install-replace-launch-linux.sh [options]

Build the current v2rayN.Desktop project, replace the local /opt/v2rayN app files,
stop the currently running v2rayN/core processes, and launch the replaced app.

This script does not touch ~/.local/share/v2rayN, so existing GUI config,
subscriptions, profiles, logs, core binaries, and generated core configs are kept.

Options:
  --install-dir DIR   App install directory. Default: /opt/v2rayN
  --launcher PATH     Launcher path. Default: /usr/bin/v2rayn
  --rid RID           .NET runtime id. Auto: linux-x64 or linux-arm64
  --configuration CFG Build configuration. Default: Release
  --no-build          Use existing publish output
  --no-start          Replace files but do not start v2rayN
  --backup            Backup install dir to DIR.backup-YYYYmmdd-HHMMSS first
  -h, --help          Show this help
USAGE
}

parse_args() {
  while [[ $# -gt 0 ]]; do
    case "$1" in
      --install-dir) INSTALL_DIR="${2:-}"; shift 2 ;;
      --launcher) LAUNCHER="${2:-}"; shift 2 ;;
      --rid) RID="${2:-}"; shift 2 ;;
      --configuration) CONFIGURATION="${2:-}"; shift 2 ;;
      --no-build) DO_BUILD=0; shift ;;
      --no-start) DO_START=0; shift ;;
      --backup) DO_BACKUP=1; shift ;;
      -h|--help) usage; exit 0 ;;
      *) die "unknown option: $1" ;;
    esac
  done
}

detect_rid() {
  if [[ -n "$RID" ]]; then
    return
  fi

  case "$(uname -m)" in
    x86_64) RID="linux-x64" ;;
    aarch64|arm64) RID="linux-arm64" ;;
    *) die "unsupported CPU architecture: $(uname -m)" ;;
  esac
}

require_tools() {
  [[ "$(uname -s)" == "Linux" ]] || die "this script only supports Linux"
  command -v dotnet >/dev/null 2>&1 || die "dotnet is required"
  command -v rsync >/dev/null 2>&1 || die "rsync is required"
  command -v pgrep >/dev/null 2>&1 || die "pgrep is required"
  command -v nohup >/dev/null 2>&1 || die "nohup is required"
}

find_paths() {
  SCRIPT_DIR="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
  REPO_ROOT="$(cd -- "$SCRIPT_DIR/.." && pwd)"
  PROJECT="$REPO_ROOT/v2rayN/v2rayN.Desktop/v2rayN.Desktop.csproj"
  PUBLISH_DIR="$REPO_ROOT/v2rayN/v2rayN.Desktop/bin/$CONFIGURATION/net10.0/$RID/publish"
  USER_DATA_DIR="${HOME}/.local/share/v2rayN"

  [[ -f "$PROJECT" ]] || die "project not found: $PROJECT"
}

publish_app() {
  if [[ "$DO_BUILD" -eq 0 ]]; then
    log "Skipping build; using existing publish output"
    return
  fi

  log "Publishing $PROJECT ($CONFIGURATION, $RID)"
  dotnet publish "$PROJECT" \
    -c "$CONFIGURATION" \
    -r "$RID" \
    --self-contained true \
    -p:PublishSingleFile=false \
    -p:DebugType=none \
    -p:DebugSymbols=false
}

verify_publish_output() {
  [[ -d "$PUBLISH_DIR" ]] || die "publish dir not found: $PUBLISH_DIR"
  [[ -x "$PUBLISH_DIR/v2rayN" ]] || die "published executable not found: $PUBLISH_DIR/v2rayN"
  [[ -f "$PUBLISH_DIR/v2rayN.dll" ]] || die "published v2rayN.dll not found"
  [[ -f "$PUBLISH_DIR/ServiceLib.dll" ]] || die "published ServiceLib.dll not found"
}

verify_installed_output() {
  log "Verifying installed app files"

  [[ -x "$INSTALL_DIR/v2rayN" ]] || die "installed executable not found: $INSTALL_DIR/v2rayN"
  [[ -f "$INSTALL_DIR/v2rayN.dll" ]] || die "installed v2rayN.dll not found"
  [[ -f "$INSTALL_DIR/ServiceLib.dll" ]] || die "installed ServiceLib.dll not found"

  cmp -s "$PUBLISH_DIR/v2rayN" "$INSTALL_DIR/v2rayN" \
    || die "installed executable differs from publish output: $INSTALL_DIR/v2rayN"
  cmp -s "$PUBLISH_DIR/v2rayN.dll" "$INSTALL_DIR/v2rayN.dll" \
    || die "installed v2rayN.dll differs from publish output: $INSTALL_DIR/v2rayN.dll"
  cmp -s "$PUBLISH_DIR/ServiceLib.dll" "$INSTALL_DIR/ServiceLib.dll" \
    || die "installed ServiceLib.dll differs from publish output: $INSTALL_DIR/ServiceLib.dll"
}

stop_processes() {
  log "Stopping running v2rayN GUI and core processes"
  stop_by_pattern "v2rayN GUI" "^${INSTALL_DIR}/v2rayN($| )"
  stop_by_pattern "v2rayN cores" "v2rayN/bin/.*/(sing-box|xray|mihomo)( |$)"
}

stop_by_pattern() {
  local name="$1"
  local pattern="$2"
  local pids=""
  local remaining=""

  pids="$(pgrep -f "$pattern" || true)"
  if [[ -z "$pids" ]]; then
    log "$name: not running"
    return
  fi

  log "$name: stopping $pids"
  kill $pids 2>/dev/null || true

  for _ in $(seq 1 25); do
    sleep 0.2
    remaining="$(pgrep -f "$pattern" || true)"
    [[ -n "$remaining" ]] || return
  done

  remaining="$(pgrep -f "$pattern" || true)"
  if [[ -n "$remaining" ]]; then
    log "$name: force stopping $remaining"
    kill -9 $remaining 2>/dev/null || true
  fi
}

install_app() {
  local helper=""
  helper="$(mktemp)"
  trap 'rm -f "$helper"' RETURN

  cat >"$helper" <<'ROOT_SCRIPT'
set -euo pipefail

case "$INSTALL_DIR" in
  /opt/v2rayN|/opt/v2rayN/*) ;;
  *) echo "refuse unsafe install dir: $INSTALL_DIR" >&2; exit 2 ;;
esac

mkdir -p "$INSTALL_DIR"

if [[ "$DO_BACKUP" == "1" && -e "$INSTALL_DIR" ]]; then
  cp -a "$INSTALL_DIR" "$INSTALL_DIR.backup-$(date +%Y%m%d-%H%M%S)"
fi

rsync -a --chown=root:root --exclude="/publish/" "$PUBLISH_DIR/" "$INSTALL_DIR/"
chmod 755 "$INSTALL_DIR/v2rayN"

mkdir -p "$(dirname "$LAUNCHER")"
cat >"$LAUNCHER" <<LAUNCHER_SCRIPT
#!/usr/bin/env bash
set -euo pipefail

cd "$INSTALL_DIR"
exec "$INSTALL_DIR/v2rayN" "\$@"
LAUNCHER_SCRIPT
chmod 755 "$LAUNCHER"
ROOT_SCRIPT

  log "Installing app files into $INSTALL_DIR"
  log "User data preserved: $USER_DATA_DIR"
  run_root "$helper"
}

run_root() {
  local helper="$1"

  if [[ "${EUID:-$(id -u)}" -eq 0 ]]; then
    INSTALL_DIR="$INSTALL_DIR" \
    LAUNCHER="$LAUNCHER" \
    PUBLISH_DIR="$PUBLISH_DIR" \
    DO_BACKUP="$DO_BACKUP" \
    /usr/bin/env bash "$helper"
    return
  fi

  if command -v pkexec >/dev/null 2>&1 && { [[ -n "${DISPLAY:-}" ]] || [[ -n "${WAYLAND_DISPLAY:-}" ]]; }; then
    pkexec env \
      DISPLAY="${DISPLAY:-}" \
      WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-}" \
      XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-}" \
      INSTALL_DIR="$INSTALL_DIR" \
      LAUNCHER="$LAUNCHER" \
      PUBLISH_DIR="$PUBLISH_DIR" \
      DO_BACKUP="$DO_BACKUP" \
      /usr/bin/env bash "$helper"
    return
  fi

  if command -v sudo >/dev/null 2>&1; then
    sudo env \
      INSTALL_DIR="$INSTALL_DIR" \
      LAUNCHER="$LAUNCHER" \
      PUBLISH_DIR="$PUBLISH_DIR" \
      DO_BACKUP="$DO_BACKUP" \
      /usr/bin/env bash "$helper"
    return
  fi

  die "pkexec or sudo is required to write $INSTALL_DIR"
}

start_app() {
  if [[ "$DO_START" -eq 0 ]]; then
    log "Skipping launch"
    return
  fi

  local start_cmd="$LAUNCHER"
  [[ -x "$start_cmd" ]] || start_cmd="$INSTALL_DIR/v2rayN"
  [[ -x "$start_cmd" ]] || die "launcher not executable: $start_cmd"

  log "Launching $start_cmd"
  nohup "$start_cmd" >/tmp/v2rayn-start.log 2>&1 &
  sleep 1

  if pgrep -a -f "^${INSTALL_DIR}/v2rayN($| )" >/dev/null; then
    pgrep -a -f "^${INSTALL_DIR}/v2rayN($| )"
  else
    echo "warning: v2rayN process was not detected; see /tmp/v2rayn-start.log" >&2
  fi
}

main() {
  parse_args "$@"
  detect_rid
  require_tools
  find_paths
  publish_app
  verify_publish_output
  install_app
  verify_installed_output
  stop_processes
  start_app
  log "Done"
}

main "$@"
