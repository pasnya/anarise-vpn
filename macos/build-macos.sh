#!/bin/bash
set -e

ARCH="${1:-osx-arm64}"
echo "Building ANARISE VPN for macOS architecture: $ARCH..."

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

PUBLISH_DIR="$SCRIPT_DIR/bin/Release/net8.0/$ARCH/publish"
APP_BUNDLE="$SCRIPT_DIR/Anarise.app"

# 1. Publish .NET 8 binary
dotnet publish "$SCRIPT_DIR/Anarise.Mac.csproj" -c Release -r "$ARCH" --self-contained false -o "$PUBLISH_DIR"

# 2. Assemble .app bundle
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

cp "$SCRIPT_DIR/Info.plist" "$APP_BUNDLE/Contents/Info.plist"
cp -r "$PUBLISH_DIR/"* "$APP_BUNDLE/Contents/MacOS/"
chmod +x "$APP_BUNDLE/Contents/MacOS/Anarise"

echo "ANARISE VPN successfully packaged into: $APP_BUNDLE"
