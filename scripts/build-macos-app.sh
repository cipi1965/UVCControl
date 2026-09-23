#!/bin/sh
# Builds "UVC Control.app" into ./build (self-contained publish + Info.plist + ad-hoc signature).
# Usage: [VERSION=1.2.3] scripts/build-macos-app.sh [osx-arm64|osx-x64]
set -e
cd "$(dirname "$0")/.."
RID="${1:-osx-arm64}"
OUT="build/publish-$RID"
APP="build/UVC Control.app"
VERSION="${VERSION:-1.0.0}"

dotnet publish src/UVCControl.App -c Release -r "$RID" --self-contained -o "$OUT" -p:Version="$VERSION"

rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS"
cp -R "$OUT/" "$APP/Contents/MacOS/"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleExecutable</key><string>UVCControl</string>
    <key>CFBundleIdentifier</key><string>it.cvm.UVCControl.Avalonia</string>
    <key>CFBundleName</key><string>UVC Control</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>$VERSION</string>
    <key>CFBundleVersion</key><string>$VERSION</string>
    <key>LSMinimumSystemVersion</key><string>12.0</string>
    <key>NSHighResolutionCapable</key><true/>
    <key>NSCameraUsageDescription</key><string>Shows a live preview while you adjust camera controls.</string>
</dict>
</plist>
PLIST
codesign --force --deep --sign - "$APP"
echo "Built $APP"
