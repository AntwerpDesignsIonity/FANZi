#!/usr/bin/env bash
# FANZi IO-nity — macOS app bundle + disk image from a self-contained `dotnet publish` folder. Runs on macOS.
# Usage: package-macos.sh <version> <rid: osx-arm64|osx-x64> <publish-dir> <out-dir> <icon-png>
# Produces: FANZi-IO-nity-v<ver>-macOS-<arm64|x64>.dmg and .zip (ad-hoc signed; not notarised —
#           first launch: right-click the app → Open, or: xattr -dr com.apple.quarantine "/Applications/FANZi IO-nity.app").
# (c) 2018-2026 Ionity Global (Pty) Ltd — Policy 986 AED.
set -euo pipefail
VERSION="$1"; RID="$2"; PUB="$(cd "$3" && pwd)"; OUT="$(mkdir -p "$4" && cd "$4" && pwd)"; ICON="$(cd "$(dirname "$5")" && pwd)/$(basename "$5")"
case "$RID" in osx-arm64) ARCH=arm64 ;; osx-x64) ARCH=x64 ;; *) echo "unsupported rid $RID" >&2; exit 2 ;; esac
APP_NAME="FANZi IO-nity"
WORK="$(mktemp -d)"; trap 'rm -rf "$WORK"' EXIT
APP="$WORK/stage/$APP_NAME.app"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -a "$PUB/." "$APP/Contents/MacOS/"; rm -f "$APP/Contents/MacOS/"*.pdb
chmod +x "$APP/Contents/MacOS/Fanzi.FanControl"

# Icon: 1024 px master → iconset → .icns
SET="$WORK/fanzi.iconset"; mkdir -p "$SET"
for s in 16 32 128 256 512; do
  sips -z $s $s "$ICON" --out "$SET/icon_${s}x${s}.png" >/dev/null
  sips -z $((s*2)) $((s*2)) "$ICON" --out "$SET/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$SET" -o "$APP/Contents/Resources/fanzi.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>${APP_NAME}</string>
  <key>CFBundleDisplayName</key><string>${APP_NAME}</string>
  <key>CFBundleIdentifier</key><string>today.ionity.fanzi</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleExecutable</key><string>Fanzi.FanControl</string>
  <key>CFBundleIconFile</key><string>fanzi.icns</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSHumanReadableCopyright</key><string>© 2018-2026 Ionity Global (Pty) Ltd</string>
</dict>
</plist>
PLIST

# Ad-hoc signature (required for arm64 code to run); notarisation needs an Apple Developer ID.
codesign --force --deep --sign - "$APP"
codesign --verify --deep --strict "$APP"

BASE="FANZi-IO-nity-v${VERSION}-macOS-${ARCH}"
ditto -c -k --keepParent "$APP" "$OUT/$BASE.zip"
ln -s /Applications "$WORK/stage/Applications"
hdiutil create -volname "$APP_NAME ${VERSION}" -srcfolder "$WORK/stage" -ov -format UDZO "$OUT/$BASE.dmg" >/dev/null
ls -la "$OUT"
