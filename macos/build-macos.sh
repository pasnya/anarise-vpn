#!/bin/bash
set -e

ARCH="${1:-osx-arm64}"
echo "Building ANARISE VPN for macOS architecture: $ARCH..."

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

PUBLISH_DIR="$SCRIPT_DIR/bin/Release/net8.0/$ARCH/publish"
APP_BUNDLE="$SCRIPT_DIR/Anarise.app"

# 1. Publish self-contained .NET 8 binary so macOS users do not need .NET installed
dotnet publish "$SCRIPT_DIR/Anarise.Mac.csproj" \
  -c Release \
  -r "$ARCH" \
  --self-contained true \
  -p:PublishSingleFile=false \
  -o "$PUBLISH_DIR"

# 2. Assemble .app bundle
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

cp "$SCRIPT_DIR/Info.plist" "$APP_BUNDLE/Contents/Info.plist"
cp -R "$PUBLISH_DIR/"* "$APP_BUNDLE/Contents/MacOS/"
chmod +x "$APP_BUNDLE/Contents/MacOS/Anarise"

# 3. Create icon if sips and iconutil are available
LOGO_PNG="$ROOT_DIR/windows/Resources/logo_transparent.png"
if command -v iconutil >/dev/null 2>&1 && command -v sips >/dev/null 2>&1 && [ -f "$LOGO_PNG" ]; then
  echo "Generating AppIcon.icns..."
  ICONSET="$SCRIPT_DIR/AppIcon.iconset"
  rm -rf "$ICONSET"
  mkdir -p "$ICONSET"
  sips -z 16 16     "$LOGO_PNG" --out "$ICONSET/icon_16x16.png" >/dev/null 2>&1 || true
  sips -z 32 32     "$LOGO_PNG" --out "$ICONSET/icon_16x16@2x.png" >/dev/null 2>&1 || true
  sips -z 32 32     "$LOGO_PNG" --out "$ICONSET/icon_32x32.png" >/dev/null 2>&1 || true
  sips -z 64 64     "$LOGO_PNG" --out "$ICONSET/icon_32x32@2x.png" >/dev/null 2>&1 || true
  sips -z 128 128   "$LOGO_PNG" --out "$ICONSET/icon_128x128.png" >/dev/null 2>&1 || true
  sips -z 256 256   "$LOGO_PNG" --out "$ICONSET/icon_128x128@2x.png" >/dev/null 2>&1 || true
  sips -z 256 256   "$LOGO_PNG" --out "$ICONSET/icon_256x256.png" >/dev/null 2>&1 || true
  sips -z 512 512   "$LOGO_PNG" --out "$ICONSET/icon_256x256@2x.png" >/dev/null 2>&1 || true
  sips -z 512 512   "$LOGO_PNG" --out "$ICONSET/icon_512x512.png" >/dev/null 2>&1 || true
  sips -z 1024 1024 "$LOGO_PNG" --out "$ICONSET/icon_512x512@2x.png" >/dev/null 2>&1 || true
  iconutil -c icns "$ICONSET" -o "$APP_BUNDLE/Contents/Resources/AppIcon.icns" >/dev/null 2>&1 || true
  rm -rf "$ICONSET"
fi

# 4. Clear extended attributes and perform ad-hoc code signing of all Mach-O binaries & bundle
if command -v xattr >/dev/null 2>&1; then
  xattr -cr "$APP_BUNDLE" || true
fi

if command -v codesign >/dev/null 2>&1; then
  echo "Ad-hoc signing Mach-O libraries and Anarise.app..."
  find "$APP_BUNDLE/Contents/MacOS" -type f \( -name "*.dylib" -o -name "createdump" -o -name "Anarise" \) -exec codesign --force --sign - {} \; || true
  codesign --force --deep --sign - "$APP_BUNDLE" || true
fi

echo "ANARISE VPN successfully packaged into: $APP_BUNDLE"

# 5. Create DMG installer with Applications symlink and Gatekeeper helper script
DMG_STAGING="$SCRIPT_DIR/dmg-staging-$ARCH"
DMG_OUTPUT="$SCRIPT_DIR/AnariseVPN-macOS-$ARCH.dmg"

rm -rf "$DMG_STAGING" "$DMG_OUTPUT"
mkdir -p "$DMG_STAGING"
cp -R "$APP_BUNDLE" "$DMG_STAGING/"
ln -s /Applications "$DMG_STAGING/Applications"

cat > "$DMG_STAGING/Установить и Открыть Anarise.command" << 'EOF'
#!/bin/bash
set -e
DIR="$(cd "$(dirname "$0")" && pwd)"
echo "Установка ANARISE VPN в /Applications..."
rm -rf "/Applications/Anarise.app"
cp -R "$DIR/Anarise.app" "/Applications/Anarise.app"
echo "Снятие карантина Gatekeeper (com.apple.quarantine)..."
xattr -cr "/Applications/Anarise.app" || true
codesign --force --deep --sign - "/Applications/Anarise.app" >/dev/null 2>&1 || true
echo "Запуск ANARISE VPN..."
open "/Applications/Anarise.app"
EOF
chmod +x "$DMG_STAGING/Установить и Открыть Anarise.command"

if command -v hdiutil >/dev/null 2>&1; then
  echo "Creating DMG image: $DMG_OUTPUT..."
  hdiutil create -volname "ANARISE VPN" -srcfolder "$DMG_STAGING" -ov -format UDZO "$DMG_OUTPUT"
  rm -rf "$DMG_STAGING"
  echo "DMG successfully created: $DMG_OUTPUT"
else
  echo "hdiutil not available in current environment; skipping DMG generation."
fi
