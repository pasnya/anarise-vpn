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

# Fail early if publish silently produced a framework-dependent app. Such a
# bundle will bounce once in the Dock and exit on Macs without .NET 8 installed.
for runtime_file in libhostfxr.dylib libhostpolicy.dylib libcoreclr.dylib; do
  if [ ! -f "$PUBLISH_DIR/$runtime_file" ]; then
    echo "ERROR: self-contained macOS runtime is incomplete: missing $runtime_file in $PUBLISH_DIR" >&2
    echo "The app bundle would require a separately installed .NET 8 runtime." >&2
    exit 1
  fi
done

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

# 5. Create DMG installer with Applications symlink and clear instruction file
DMG_STAGING="$SCRIPT_DIR/dmg-staging-$ARCH"
DMG_OUTPUT="$SCRIPT_DIR/AnariseVPN-macOS-$ARCH.dmg"

rm -rf "$DMG_STAGING" "$DMG_OUTPUT"
mkdir -p "$DMG_STAGING"
cp -R "$APP_BUNDLE" "$DMG_STAGING/"
ln -s /Applications "$DMG_STAGING/Applications"


# Ensure proper permissions for all bundle contents
chmod -R a+rX "$DMG_STAGING/Anarise.app" || true
chmod +x "$DMG_STAGING/Anarise.app/Contents/MacOS/Anarise" || true

cat > "$DMG_STAGING/КАК ОТКРЫТЬ ANARISE.txt" << 'EOF'
ИНСТРУКЦИЯ ПО УСТАНОВКЕ И ЗАПУСКУ ANARISE VPN (macOS):
======================================================

1. ПЕРЕТАЩИТЕ Anarise в папку «Applications» (Программы).

2. ПЕРВЫЙ ЗАПУСК (Защита macOS Gatekeeper):
   Так как ANARISE VPN — бесплатное приложение с открытым кодом,
   macOS при первом открытии покажет стандартное предупреждение безопасности.

   ЕСТЬ 2 ПРОСТЫХ СПОСОБА ЕГО ОТКРЫТЬ:

   СПОСОБ 1 (через Настройки macOS — без Терминала):
   1. Запустите Anarise из папки «Программы». При появлении окна нажмите «Готово» (или «Отмена»).
   2. Откройте «Системные настройки» -> «Конфиденциальность и безопасность».
   3. Прокрутите вниз до раздела «Безопасность». Там появится сообщение:
      «Приложение "Anarise.app" было заблокировано...»
   4. Нажмите кнопку «Всё равно открыть» (Open Anyway) и введите пароль Mac.

   СПОСОБ 2 (через Терминал — 1 секунда):
   Откройте программу «Терминал» и выполните команду:
   xattr -cr /Applications/Anarise.app

   После этого приложение будет всегда открываться обычным двойным кликом!
======================================================
EOF

if command -v xattr >/dev/null 2>&1; then
  xattr -cr "$DMG_STAGING" || true
fi

if command -v hdiutil >/dev/null 2>&1; then
  echo "Creating DMG image: $DMG_OUTPUT..."
  hdiutil create -volname "ANARISE VPN" -srcfolder "$DMG_STAGING" -ov -format UDZO "$DMG_OUTPUT"
  rm -rf "$DMG_STAGING"
  echo "DMG successfully created: $DMG_OUTPUT"
else
  echo "hdiutil not available in current environment; skipping DMG generation."
fi
