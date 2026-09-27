#!/usr/bin/env bash
# Publishes Cards.Server from this checkout into a new release folder, switches the
# live app over to it, and checks it came up. Run from the repo root — the GitHub
# Actions runner does, on every push to the deploy branch — or by hand.
#
#   /srv/cards/releases/<time>-<commit>/   one folder per deploy (the last 5 are kept)
#   /srv/cards/current  ->  the release being served
#
# If the new release does not answer its health check, it switches back to the one
# that was running and fails, so a bad deploy leaves the old version up.
set -euo pipefail

ROOT=/srv/cards
PORT=5280
SERVICE=cards

sha=$(git rev-parse --short HEAD)
release="$ROOT/releases/$(date +%Y%m%d-%H%M%S)-$sha"
previous=$(readlink -f "$ROOT/current" 2>/dev/null || true)

echo "Publishing $sha to $release"
dotnet publish src/Cards.Server/Cards.Server.csproj -c Release -o "$release" --nologo

switch_to() {
    ln -sfn "$1" "$ROOT/current"
    sudo /usr/bin/systemctl restart "$SERVICE"
}

healthy() {
    for _ in $(seq 1 30); do
        curl -fsS "http://127.0.0.1:$PORT/health" >/dev/null 2>&1 && return 0
        sleep 1
    done
    return 1
}

switch_to "$release"
if healthy; then
    echo "Live: $release"
else
    echo "The new release did not come up." >&2
    if [[ -n "$previous" && -d "$previous" ]]; then
        echo "Switching back to $previous" >&2
        switch_to "$previous"
    fi
    exit 1
fi

# Keep the five newest releases, and never the one being served.
current=$(readlink -f "$ROOT/current")
ls -1dt "$ROOT"/releases/*/ | tail -n +6 | while read -r old; do
    [[ "$(readlink -f "$old")" == "$current" ]] || rm -rf "$old"
done
