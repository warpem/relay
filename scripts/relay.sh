#!/bin/bash
# Relay process management script.
# Usage: relay.sh {start|stop|restart|status}

set -euo pipefail

ENV_FILE="${RELAY_ENV_FILE:-$HOME/relay/relay.env}"

if [[ ! -f "$ENV_FILE" ]]; then
    echo "Error: Environment file not found: $ENV_FILE" >&2
    echo "Copy relay.env.example to $ENV_FILE and fill in values." >&2
    exit 1
fi

source "$ENV_FILE"

RELAY_HOME="${RELAY_HOME:-$HOME/relay}"
APP_DIR="$RELAY_HOME/app"
PID_FILE="$RELAY_HOME/relay.pid"
LOG_DIR="$RELAY_HOME/logs"

# ── Runtime environment ──────────────────────────────────────────────────────

# Load module system and conda
source "${LMOD_INIT}"
module load "${CONDA_MODULE}"
conda activate "${CONDA_ENV}"

export DOTNET_ROOT="${DOTNET_ROOT}"
export PATH="${DOTNET_ROOT}:${DOTNET_ROOT}/tools:$PATH"
export LD_LIBRARY_PATH="${WARP_LIB_PATH}:${LD_LIBRARY_PATH:-}"

export ASPNETCORE_ENVIRONMENT=Production
export ASPNETCORE_CONTENTROOT="$APP_DIR"

# Kestrel HTTPS configuration via environment variables
export Kestrel__Endpoints__Https__Url="https://0.0.0.0:${RELAY_PORT}"
export Kestrel__Endpoints__Https__Certificate__Path="${RELAY_CERT_PATH}"
export Kestrel__Endpoints__Https__Certificate__Password="${RELAY_CERT_PASSWORD}"

# ── Functions ────────────────────────────────────────────────────────────────

get_pid() {
    if [[ -f "$PID_FILE" ]] && kill -0 "$(cat "$PID_FILE")" 2>/dev/null; then
        cat "$PID_FILE"
    fi
}

do_start() {
    local pid
    pid=$(get_pid)
    if [[ -n "$pid" ]]; then
        echo "Relay is already running (PID $pid)"
        return 1
    fi

    if [[ ! -f "$APP_DIR/Relay.dll" ]]; then
        echo "Error: $APP_DIR/Relay.dll not found" >&2
        exit 1
    fi

    mkdir -p "$LOG_DIR"

    cd "$RELAY_HOME"
    dotnet "$APP_DIR/Relay.dll" >> "$LOG_DIR/relay.log" 2>&1 &

    echo $! > "$PID_FILE"
    disown

    echo "Relay started (PID $(cat "$PID_FILE"))"
}

do_stop() {
    local pid
    pid=$(get_pid)
    if [[ -z "$pid" ]]; then
        echo "Relay is not running"
        rm -f "$PID_FILE"
        return 0
    fi

    echo "Stopping Relay (PID $pid)..."
    kill "$pid"

    for _ in $(seq 1 10); do
        if ! kill -0 "$pid" 2>/dev/null; then
            echo "Relay stopped"
            rm -f "$PID_FILE"
            return 0
        fi
        sleep 1
    done

    echo "Relay did not stop gracefully, sending SIGKILL..."
    kill -9 "$pid" 2>/dev/null || true
    rm -f "$PID_FILE"
}

do_status() {
    local pid
    pid=$(get_pid)
    if [[ -n "$pid" ]]; then
        echo "Relay is running (PID $pid)"
    else
        echo "Relay is not running"
        rm -f "$PID_FILE"
    fi
}

# ── Main ─────────────────────────────────────────────────────────────────────

case "${1:-}" in
    start)   do_start ;;
    stop)    do_stop ;;
    restart) do_stop; do_start ;;
    status)  do_status ;;
    *)       echo "Usage: $0 {start|stop|restart|status}" >&2; exit 1 ;;
esac
