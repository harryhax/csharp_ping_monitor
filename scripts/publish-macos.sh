#!/usr/bin/env bash
# Builds a self-contained macOS .app bundle.
# Usage: scripts/publish-macos.sh [osx-arm64|osx-x64]   (default: osx-arm64)
set -euo pipefail

RID="${1:-osx-arm64}"
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT="$ROOT/artifacts/publish/$RID"
APP="$ROOT/artifacts/$RID/Network Diagnostics.app"
VERSION="$(sed -n 's:.*<Version>\(.*\)</Version>.*:\1:p' "$ROOT/Directory.Build.props")"

rm -rf "$OUT" "$APP"
mkdir -p "$(dirname "$APP")"
dotnet publish "$ROOT/NetworkDiagnostics.App/NetworkDiagnostics.App.csproj" \
  -c Release -r "$RID" --self-contained true \
  -p:PublishSingleFile=false -p:DebugType=none -o "$OUT"

mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
cp -R "$OUT/." "$APP/Contents/MacOS/"
cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Network Diagnostics</string>
  <key>CFBundleDisplayName</key><string>Network Diagnostics</string>
  <key>CFBundleIdentifier</key><string>local.networkdiagnostics.app</string>
  <key>CFBundleExecutable</key><string>NetworkDiagnostics</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleVersion</key><string>$VERSION</string>
  <key>CFBundleShortVersionString</key><string>$VERSION</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>NSLocalNetworkUsageDescription</key><string>Network Diagnostics sends ping requests to your router to measure local network latency and packet loss.</string>
</dict>
</plist>
PLIST
chmod +x "$APP/Contents/MacOS/NetworkDiagnostics"

# Ad-hoc signature so the bundle runs locally. Distribution to other Macs needs a
# Developer ID signature and notarization (see README).
codesign --force --deep --sign - "$APP" >/dev/null 2>&1 || echo "warning: ad-hoc codesign failed"

echo "Created: $APP"
