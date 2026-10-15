#!/usr/bin/env bash
# Installs (or removes) the RemoteMesh server as a systemd service on Linux.
#
#   sudo ./install-server.sh [--source DIR] [--urls URLS] [--install-dir DIR] [--dry-run]
#   sudo ./install-server.sh --uninstall
#
# The service runs as its own unprivileged user with a locked-down systemd sandbox. No secrets are written:
# provide TLS either through a reverse proxy (see nginx.conf.example) or by pointing Kestrel at certificate files
# in /etc/remotemesh/server.env (Kestrel__Certificates__Default__Path / __KeyPath), which only root and the
# service user can read.
set -euo pipefail

SOURCE_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/../server" 2>/dev/null && pwd || echo "")"
INSTALL_DIR="/opt/remotemesh/server"
URLS="http://127.0.0.1:5222"
SERVICE="remotemesh-server"
USER_NAME="remotemesh"
ENV_FILE="/etc/remotemesh/server.env"
DRY_RUN=0
UNINSTALL=0

while [[ $# -gt 0 ]]; do
  case "$1" in
    --source) SOURCE_DIR="$2"; shift 2 ;;
    --urls) URLS="$2"; shift 2 ;;
    --install-dir) INSTALL_DIR="$2"; shift 2 ;;
    --dry-run) DRY_RUN=1; shift ;;
    --uninstall) UNINSTALL=1; shift ;;
    -h|--help) sed -n '2,10p' "$0"; exit 0 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

run() {
  if [[ $DRY_RUN -eq 1 ]]; then echo "[dry-run] $*"; else "$@"; fi
}

if [[ $DRY_RUN -eq 0 && $EUID -ne 0 ]]; then
  echo "Run as root (sudo), or use --dry-run to see the steps." >&2
  exit 1
fi

if [[ $UNINSTALL -eq 1 ]]; then
  run systemctl disable --now "$SERVICE" 2>/dev/null || true
  run rm -f "/etc/systemd/system/$SERVICE.service"
  run systemctl daemon-reload
  run rm -rf "$INSTALL_DIR"
  echo "Uninstalled. Left in place: $ENV_FILE and user '$USER_NAME' (remove manually if wanted)."
  exit 0
fi

if [[ ! -f "$SOURCE_DIR/RemoteDesktop.Server.Api.dll" && $DRY_RUN -eq 0 ]]; then
  echo "No published server in '$SOURCE_DIR'. Use --source or run scripts/publish.sh first." >&2
  exit 1
fi

# Dedicated unprivileged account, no login shell, no home.
if ! id "$USER_NAME" >/dev/null 2>&1; then
  run useradd --system --no-create-home --shell /usr/sbin/nologin "$USER_NAME"
fi

run mkdir -p "$INSTALL_DIR" "$(dirname "$ENV_FILE")"
run cp -a "$SOURCE_DIR/." "$INSTALL_DIR/"
# Program files are owned by root and only readable by the service: it cannot rewrite its own binaries.
run chown -R root:"$USER_NAME" "$INSTALL_DIR"
run chmod -R u=rwX,g=rX,o= "$INSTALL_DIR"
[[ -f "$INSTALL_DIR/RemoteDesktop.Server.Api" ]] && run chmod 750 "$INSTALL_DIR/RemoteDesktop.Server.Api"

if [[ ! -f "$ENV_FILE" ]]; then
  if [[ $DRY_RUN -eq 1 ]]; then
    echo "[dry-run] write $ENV_FILE (ASPNETCORE_URLS=$URLS, mode 0640 root:$USER_NAME)"
  else
    cat > "$ENV_FILE" <<ENV
# Environment for the RemoteMesh server. Configuration keys use "__" for nesting, e.g.
#   RemoteMesh__Session__MaxTotalActiveSessions=5000
# Do not put secrets in appsettings.json; if a certificate password is unavoidable it belongs here (mode 0640).
ASPNETCORE_URLS=$URLS
ASPNETCORE_ENVIRONMENT=Production
ENV
    chown root:"$USER_NAME" "$ENV_FILE"
    chmod 0640 "$ENV_FILE"
  fi
fi

UNIT_SRC="$(dirname "${BASH_SOURCE[0]}")/remotemesh-server.service"
run install -m 0644 "$UNIT_SRC" "/etc/systemd/system/$SERVICE.service"
run systemctl daemon-reload
run systemctl enable --now "$SERVICE"
echo "Installed. Status: systemctl status $SERVICE   Logs: journalctl -u $SERVICE -f"
