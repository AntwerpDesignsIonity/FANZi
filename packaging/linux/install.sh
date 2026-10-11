#!/usr/bin/env bash
# Installs FANZi IO-nity for the current user (no root): ~/.local/opt/fanzi-io-nity,
# a 'fanzi' command in ~/.local/bin and a menu entry.
set -euo pipefail
HERE="$(cd "$(dirname "$0")" && pwd)"
DEST="$HOME/.local/opt/fanzi-io-nity"
mkdir -p "$DEST" "$HOME/.local/bin" "$HOME/.local/share/applications" "$HOME/.local/share/icons/hicolor/256x256/apps"
rm -rf "$DEST"; mkdir -p "$DEST"
cp -a "$HERE/app/." "$DEST/"
chmod +x "$DEST/Fanzi.FanControl"
ln -sf "$DEST/Fanzi.FanControl" "$HOME/.local/bin/fanzi"
cp "$HERE/fanzi-io-nity.png" "$HOME/.local/share/icons/hicolor/256x256/apps/fanzi-io-nity.png"
sed "s|^Exec=.*|Exec=$DEST/Fanzi.FanControl|" "$HERE/fanzi-io-nity.desktop" > "$HOME/.local/share/applications/fanzi-io-nity.desktop"
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q "$HOME/.local/share/applications" || true
echo "FANZi IO-nity installed. Start it from your app menu or run: fanzi"
case ":$PATH:" in *":$HOME/.local/bin:"*) ;; *) echo "Note: add ~/.local/bin to your PATH to use the 'fanzi' command." ;; esac
