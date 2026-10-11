#!/usr/bin/env bash
# Removes the per-user FANZi IO-nity install (settings in ~/.config/FANZI are kept).
set -euo pipefail
rm -rf "$HOME/.local/opt/fanzi-io-nity"
rm -f "$HOME/.local/bin/fanzi" "$HOME/.local/share/applications/fanzi-io-nity.desktop" \
      "$HOME/.local/share/icons/hicolor/256x256/apps/fanzi-io-nity.png"
echo "FANZi IO-nity removed."
