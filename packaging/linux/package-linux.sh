#!/usr/bin/env bash
# FANZi IO-nity — Linux packages from a self-contained `dotnet publish` folder.
# Usage: package-linux.sh <version> <rid: linux-x64|linux-arm64> <publish-dir> <out-dir> <icon-png>
# Produces: FANZi-IO-nity-v<ver>-<rid>.tar.gz (portable + install.sh), FANZi-IO-nity-v<ver>-<arch>.deb,
#           and on x64 FANZi-IO-nity-v<ver>-x86_64.AppImage.
# (c) 2018-2026 Ionity Global (Pty) Ltd — Policy 986 AED.
set -euo pipefail
VERSION="$1"; RID="$2"; PUB="$(cd "$3" && pwd)"; OUT="$(mkdir -p "$4" && cd "$4" && pwd)"; ICON="$(cd "$(dirname "$5")" && pwd)/$(basename "$5")"
HERE="$(cd "$(dirname "$0")" && pwd)"
case "$RID" in
  linux-x64)   DEB_ARCH=amd64; APPIMAGE_ARCH=x86_64 ;;
  linux-arm64) DEB_ARCH=arm64; APPIMAGE_ARCH=aarch64 ;;
  *) echo "unsupported rid $RID" >&2; exit 2 ;;
esac
NAME="FANZi-IO-nity-v${VERSION}-${RID}"
WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
chmod +x "$PUB/Fanzi.FanControl"

# ── 1. Portable tarball with a per-user installer ──
T="$WORK/$NAME"; mkdir -p "$T/app"
cp -a "$PUB/." "$T/app/"; rm -f "$T/app/"*.pdb
cp "$ICON" "$T/fanzi-io-nity.png"
cp "$HERE/fanzi-io-nity.desktop" "$T/"
cp "$HERE/install.sh" "$HERE/uninstall.sh" "$HERE/enable-fan-control.sh" "$T/"
chmod +x "$T/"*.sh
cat > "$T/README.txt" <<TXT
FANZi IO-nity ${VERSION} for Linux (${RID}) — Ionity Global (Pty) Ltd
Run without installing:  ./app/Fanzi.FanControl
Install for this user:   ./install.sh        (menu entry + 'fanzi' command in ~/.local/bin)
Fan control (optional):  sudo ./enable-fan-control.sh   (lets your user write the hwmon PWM files)
Remove:                  ./uninstall.sh
Sensors come from the kernel's hwmon drivers: run 'sudo sensors-detect' (lm-sensors) if none show up.
https://www.ionity.co.za/software.html#fanzi
TXT
tar -C "$WORK" -czf "$OUT/$NAME.tar.gz" "$NAME"

# ── 2. Debian / Ubuntu package ──
D="$WORK/deb"
mkdir -p "$D/DEBIAN" "$D/opt/fanzi-io-nity" "$D/usr/bin" "$D/usr/share/applications" "$D/usr/share/icons/hicolor/256x256/apps"
cp -a "$PUB/." "$D/opt/fanzi-io-nity/"; rm -f "$D/opt/fanzi-io-nity/"*.pdb
cp "$HERE/enable-fan-control.sh" "$D/opt/fanzi-io-nity/"
ln -s /opt/fanzi-io-nity/Fanzi.FanControl "$D/usr/bin/fanzi"
sed 's|^Exec=.*|Exec=/opt/fanzi-io-nity/Fanzi.FanControl|' "$HERE/fanzi-io-nity.desktop" > "$D/usr/share/applications/fanzi-io-nity.desktop"
cp "$ICON" "$D/usr/share/icons/hicolor/256x256/apps/fanzi-io-nity.png"
SIZE=$(du -sk "$D/opt" | cut -f1)
cat > "$D/DEBIAN/control" <<CTL
Package: fanzi-io-nity
Version: ${VERSION}
Section: utils
Priority: optional
Architecture: ${DEB_ARCH}
Installed-Size: ${SIZE}
Depends: libc6, libfontconfig1, libx11-6, libice6, libsm6
Recommends: lm-sensors
Maintainer: Ionity Global (Pty) Ltd <info@ionity.today>
Homepage: https://www.ionity.co.za/software.html#fanzi
Description: FANZi IO-nity - AI thermal intelligence, fan control and RGB
 Live CPU / GPU temperatures, fan curves, RGB Studio (OpenRGB) and the
 thermal frame, with the offline AEDi assistant. Free from Ionity Global.
CTL
cat > "$D/DEBIAN/postinst" <<'POST'
#!/bin/sh
set -e
command -v update-desktop-database >/dev/null 2>&1 && update-desktop-database -q /usr/share/applications || true
command -v gtk-update-icon-cache >/dev/null 2>&1 && gtk-update-icon-cache -q /usr/share/icons/hicolor || true
exit 0
POST
chmod 755 "$D/DEBIAN/postinst"
dpkg-deb --root-owner-group --build "$D" "$OUT/FANZi-IO-nity-v${VERSION}-${DEB_ARCH}.deb"

# ── 3. AppImage (x64 runner can only run the x86_64 appimagetool) ──
if [ "$APPIMAGE_ARCH" = "x86_64" ] && [ "$(uname -m)" = "x86_64" ]; then
  A="$WORK/FANZi.AppDir"; mkdir -p "$A/usr/bin"
  cp -a "$PUB/." "$A/usr/bin/"; rm -f "$A/usr/bin/"*.pdb
  cp "$ICON" "$A/fanzi-io-nity.png"
  sed 's|^Exec=.*|Exec=Fanzi.FanControl|' "$HERE/fanzi-io-nity.desktop" > "$A/fanzi-io-nity.desktop"
  cat > "$A/AppRun" <<'RUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
exec "$HERE/usr/bin/Fanzi.FanControl" "$@"
RUN
  chmod +x "$A/AppRun"
  TOOL="$WORK/appimagetool"
  curl -fsSL -o "$TOOL" "https://github.com/AppImage/appimagetool/releases/download/continuous/appimagetool-x86_64.AppImage"
  chmod +x "$TOOL"
  ARCH=x86_64 "$TOOL" --appimage-extract-and-run "$A" "$OUT/FANZi-IO-nity-v${VERSION}-x86_64.AppImage"
fi
ls -la "$OUT"
