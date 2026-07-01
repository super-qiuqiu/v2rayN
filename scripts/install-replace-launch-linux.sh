#!/usr/bin/env bash
set -euo pipefail

CONFIGURATION="Release"
INSTALL_DIR="/opt/v2rayN"
LAUNCHER="/usr/bin/v2rayn"
RID=""
DO_BACKUP=0
DO_BUILD=1
DO_START=1
DETACHED_LOG="/tmp/v2rayn-install-replace-launch.log"

die() {
  echo "error: $*" >&2
  exit 1
}

log() {
  echo "==> $*"
}

debug() {
  echo "[debug] $*"
}

debug_process_matches() {
  local label="$1"
  local pattern="$2"
  debug "$label pattern: $pattern"
  local matches
  matches="$(pgrep -a -f "$pattern" || true)"
  if [[ -z "$matches" ]]; then
    debug "$label matches: <none>"
  else
    debug "$label matches:"
    printf '%s\n' "$matches" | sed 's/^/[debug]   /'
  fi
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

has_running_gui() {
  debug_process_matches "running GUI check" "^${INSTALL_DIR}/v2rayN($| )"
  pgrep -f "^${INSTALL_DIR}/v2rayN($| )" >/dev/null 2>&1
}

stop_processes() {
  log "Stopping running v2rayN GUI and core processes"
  debug "stop_processes: pid=$$ ppid=$PPID pwd=$(pwd)"
  debug_process_matches "before stop: GUI" "^${INSTALL_DIR}/v2rayN($| )"
  debug_process_matches "before stop: cores" "v2rayN/bin/.*/(sing-box|xray|mihomo)( |$)"
  stop_by_pattern "v2rayN GUI" "^${INSTALL_DIR}/v2rayN($| )"
  stop_by_pattern "v2rayN cores" "v2rayN/bin/.*/(sing-box|xray|mihomo)( |$)"
  debug_process_matches "after stop: GUI" "^${INSTALL_DIR}/v2rayN($| )"
  debug_process_matches "after stop: cores" "v2rayN/bin/.*/(sing-box|xray|mihomo)( |$)"
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
  debug "$name: sending SIGTERM to: $(echo "$pids" | tr '\n' ' ')"
  kill $pids 2>/dev/null || true

  for _ in $(seq 1 25); do
    sleep 0.2
    remaining="$(pgrep -f "$pattern" || true)"
    [[ -n "$remaining" ]] || return 0
  done

  remaining="$(pgrep -f "$pattern" || true)"
  if [[ -n "$remaining" ]]; then
    log "$name: force stopping $remaining"
    debug "$name: sending SIGKILL to: $(echo "$remaining" | tr '\n' ' ')"
    kill -9 $remaining 2>/dev/null || true
  fi
}

install_app() {
  local helper
  helper="$(mktemp)"
  trap "rm -f '$helper'" RETURN

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
  debug "install_app: helper=$helper publish=$PUBLISH_DIR launcher=$LAUNCHER backup=$DO_BACKUP"
  run_root "$helper"
  debug "install_app: root helper completed with rc=$?"
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

  local launcher_exists launcher_exec install_exists install_exec
  launcher_exists="$(test -e "$LAUNCHER" && echo yes || echo no)"
  launcher_exec="$(test -x "$LAUNCHER" && echo yes || echo no)"
  install_exists="$(test -e "$INSTALL_DIR/v2rayN" && echo yes || echo no)"
  install_exec="$(test -x "$INSTALL_DIR/v2rayN" && echo yes || echo no)"

  log "Launching $start_cmd"
  debug "start_app: pid=$$ ppid=$PPID pwd=$(pwd) HOME=${HOME:-} DISPLAY=${DISPLAY:-} WAYLAND_DISPLAY=${WAYLAND_DISPLAY:-} XDG_RUNTIME_DIR=${XDG_RUNTIME_DIR:-}"
  debug "start_app: launcher exists=$launcher_exists executable=$launcher_exec path=$LAUNCHER"
  debug "start_app: install exe exists=$install_exists executable=$install_exec path=$INSTALL_DIR/v2rayN"
  : >/tmp/v2rayn-start.log

  local started_pid=""
  if [[ "${V2RAYN_INSTALL_DETACHED:-0}" == "1" ]] && command -v systemd-run >/dev/null 2>&1 && systemctl --user show-environment >/dev/null 2>&1; then
    local gui_unit="v2rayn-gui-$(date +%s)-$$"
    debug "start_app: launching GUI in separate systemd user unit=$gui_unit"
    systemd-run --user --collect --unit "$gui_unit" \
      --property="StandardOutput=append:/tmp/v2rayn-start.log" \
      --property="StandardError=append:/tmp/v2rayn-start.log" \
      env \
        HOME="${HOME:-}" \
        USER="${USER:-}" \
        DISPLAY="${DISPLAY:-}" \
        WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-}" \
        XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-}" \
        DBUS_SESSION_BUS_ADDRESS="${DBUS_SESSION_BUS_ADDRESS:-}" \
        SESSION_MANAGER="${SESSION_MANAGER:-}" \
        XAUTHORITY="${XAUTHORITY:-}" \
        "$start_cmd" >/dev/null
    started_pid="systemd:$gui_unit"
  else
    nohup "$start_cmd" >/tmp/v2rayn-start.log 2>&1 &
    started_pid=$!
  fi
  debug "start_app: launcher pid/unit=$started_pid"
  sleep 1

  debug_process_matches "after launch: GUI" "^${INSTALL_DIR}/v2rayN($| )"
  if pgrep -a -f "^${INSTALL_DIR}/v2rayN($| )" >/dev/null; then
    pgrep -a -f "^${INSTALL_DIR}/v2rayN($| )"
  else
    echo "warning: v2rayN process was not detected; see /tmp/v2rayn-start.log" >&2
    if [[ -s /tmp/v2rayn-start.log ]]; then
      debug "start_app: /tmp/v2rayn-start.log follows"
      sed 's/^/[start-log] /' /tmp/v2rayn-start.log >&2
    else
      debug "start_app: /tmp/v2rayn-start.log is empty"
    fi
  fi
}

continue_detached_after_install() {
  if [[ "${V2RAYN_INSTALL_STOP_START_ONLY:-0}" == "1" ]]; then
    return 0
  fi
  if ! has_running_gui; then
    return 0
  fi

  : >"$DETACHED_LOG"
  log "Running GUI detected; detaching final stop/start phase"
  log "Detached phase log: $DETACHED_LOG"
  debug "detach: pid=$$ ppid=$PPID script=$0 args=$*"
  debug "detach: INSTALL_DIR=$INSTALL_DIR LAUNCHER=$LAUNCHER PUBLISH_DIR=$PUBLISH_DIR DO_START=$DO_START"

  local detached_pid unit_name script_abs
  script_abs="$SCRIPT_DIR/$(basename -- "$0")"
  if command -v systemd-run >/dev/null 2>&1 && systemctl --user show-environment >/dev/null 2>&1; then
    unit_name="v2rayn-restart-$(date +%s)-$$"
    debug "detach: launching helper with systemd-run --user unit=$unit_name script=$script_abs"
    if systemd-run --user --collect --unit "$unit_name" \
      --property="StandardOutput=append:$DETACHED_LOG" \
      --property="StandardError=append:$DETACHED_LOG" \
      env \
        V2RAYN_INSTALL_STOP_START_ONLY=1 \
        V2RAYN_INSTALL_DETACHED=1 \
        HOME="${HOME:-}" \
        USER="${USER:-}" \
        DISPLAY="${DISPLAY:-}" \
        WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-}" \
        XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-}" \
        DBUS_SESSION_BUS_ADDRESS="${DBUS_SESSION_BUS_ADDRESS:-}" \
        SESSION_MANAGER="${SESSION_MANAGER:-}" \
        XAUTHORITY="${XAUTHORITY:-}" \
        bash "$script_abs" "$@" --no-build >/dev/null; then
      detached_pid="systemd:$unit_name"
    else
      debug "detach: systemd-run failed, falling back"
      detached_pid=""
    fi
  fi

  if [[ -z "${detached_pid:-}" ]] && command -v setsid >/dev/null 2>&1; then
    debug "detach: launching helper with setsid -f"
    setsid -f env \
      V2RAYN_INSTALL_STOP_START_ONLY=1 \
      V2RAYN_INSTALL_DETACHED=1 \
      HOME="${HOME:-}" \
      USER="${USER:-}" \
      DISPLAY="${DISPLAY:-}" \
      WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-}" \
      XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-}" \
      DBUS_SESSION_BUS_ADDRESS="${DBUS_SESSION_BUS_ADDRESS:-}" \
      SESSION_MANAGER="${SESSION_MANAGER:-}" \
      XAUTHORITY="${XAUTHORITY:-}" \
      bash "$script_abs" "$@" --no-build </dev/null >>"$DETACHED_LOG" 2>&1
    detached_pid="$(pgrep -n -f "V2RAYN_INSTALL_STOP_START_ONLY=1|$script_abs.*--no-build" || true)"
  fi

  if [[ -z "${detached_pid:-}" ]]; then
    debug "detach: setsid not found or failed; launching helper with nohup fallback"
    nohup env \
      V2RAYN_INSTALL_STOP_START_ONLY=1 \
      V2RAYN_INSTALL_DETACHED=1 \
      HOME="${HOME:-}" \
      USER="${USER:-}" \
      DISPLAY="${DISPLAY:-}" \
      WAYLAND_DISPLAY="${WAYLAND_DISPLAY:-}" \
      XDG_RUNTIME_DIR="${XDG_RUNTIME_DIR:-}" \
      DBUS_SESSION_BUS_ADDRESS="${DBUS_SESSION_BUS_ADDRESS:-}" \
      SESSION_MANAGER="${SESSION_MANAGER:-}" \
      XAUTHORITY="${XAUTHORITY:-}" \
      bash "$script_abs" "$@" --no-build </dev/null >>"$DETACHED_LOG" 2>&1 &
    detached_pid=$!
  fi

  log "Detached helper PID/unit: ${detached_pid:-unknown}"
  debug "detach: waiting briefly to confirm helper progresses"
  sleep 0.8
  if [[ "${detached_pid:-}" == systemd:* ]]; then
    local unit="${detached_pid#systemd:}"
    debug "detach: systemd unit state: $(systemctl --user is-active "$unit" 2>/dev/null || true)"
  elif [[ -n "${detached_pid:-}" ]] && kill -0 "$detached_pid" 2>/dev/null; then
    debug "detach: helper is still running"
  else
    debug "detach: helper is not running after wait; current detached log:"
    sed 's/^/[detached-log] /' "$DETACHED_LOG" || true
  fi
  log "Build/install/verify completed; helper will stop old GUI and launch the new one."
  exit 0
}

main() {
  parse_args "$@"
  detect_rid
  require_tools
  find_paths

  debug "main: pid=$$ ppid=$PPID detached=${V2RAYN_INSTALL_STOP_START_ONLY:-0} args=$*"
  debug "main: INSTALL_DIR=$INSTALL_DIR LAUNCHER=$LAUNCHER RID=$RID CONFIGURATION=$CONFIGURATION DO_BUILD=$DO_BUILD DO_START=$DO_START"

  if [[ "${V2RAYN_INSTALL_STOP_START_ONLY:-0}" == "1" ]]; then
    trap 'debug "detached helper received SIGHUP"' HUP
    trap 'debug "detached helper received SIGTERM"; exit 143' TERM
    debug "main: running detached stop/start-only phase"
    stop_processes
    start_app
    log "Done"
    return
  fi

  publish_app
  verify_publish_output
  install_app
  verify_installed_output
  continue_detached_after_install "$@"
  stop_processes
  start_app
  log "Done"
}

main "$@"
