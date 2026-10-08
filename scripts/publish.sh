#!/usr/bin/env bash
# Builds, tests and packages RemoteMesh for Linux (tar.gz). The Windows packages come from scripts/publish.ps1.
#   ./scripts/publish.sh [--rid linux-x64] [--self-contained] [--skip-tests] [--output DIR]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
RID="linux-x64"; SELF="false"; SKIP_TESTS=0; OUT="$ROOT/artifacts"
while [[ $# -gt 0 ]]; do
  case "$1" in
    --rid) RID="$2"; shift 2 ;;
    --self-contained) SELF="true"; shift ;;
    --skip-tests) SKIP_TESTS=1; shift ;;
    --output) OUT="$2"; shift 2 ;;
    *) echo "Unknown option: $1" >&2; exit 2 ;;
  esac
done

VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props" | head -1)"
[[ -n "$VERSION" ]] || { echo "No <Version> in Directory.Build.props" >&2; exit 1; }
NAME="remotemesh-$VERSION-$RID"
STAGE="$OUT/$NAME"

cd "$ROOT"
rm -rf "$OUT"; mkdir -p "$STAGE/deploy"
dotnet build RemoteDesktop.slnx -c Release --nologo
[[ $SKIP_TESTS -eq 1 ]] || dotnet test RemoteDesktop.slnx -c Release --no-build --nologo

dotnet publish src/server/RemoteDesktop.Server.Api/RemoteDesktop.Server.Api.csproj -c Release -r "$RID" \
  --self-contained:"$SELF" -o "$STAGE/server" --nologo
dotnet publish src/RemoteDesktop.Agent/RemoteDesktop.Agent.csproj -c Release -r "$RID" \
  --self-contained:"$SELF" -o "$STAGE/agent" --nologo

cp deploy/install-server.sh deploy/remotemesh-server.service deploy/appsettings.Production.example.json \
   deploy/nginx.conf.example "$STAGE/deploy/"
cp -r docs "$STAGE/docs"; cp README.md CHANGELOG.md "$STAGE/"
printf '%s' "$VERSION" > "$STAGE/VERSION"

tar -C "$OUT" -czf "$OUT/$NAME.tar.gz" "$NAME"
(cd "$OUT" && sha256sum "$NAME.tar.gz" > SHA256SUMS.txt)
echo "Done: $OUT/$NAME.tar.gz"
