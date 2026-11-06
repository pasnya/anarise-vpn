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

# 4. Set correct permissions and clear extended attributes before code signing
chmod -R a+rX "$APP_BUNDLE"
chmod +x "$APP_BUNDLE/Contents/MacOS/Anarise"
if [ -f "$APP_BUNDLE/Contents/MacOS/createdump" ]; then
  chmod +x "$APP_BUNDLE/Contents/MacOS/createdump"
fi

if command -v xattr >/dev/null 2>&1; then
  xattr -cr "$APP_BUNDLE" || true
fi

# 5. Code signing (Inside-Out approach for .NET on macOS)
if command -v codesign >/dev/null 2>&1; then
  echo "Ad-hoc signing Mach-O binaries, dynamic libraries, and .NET assemblies inside bundle..."

  # 5a. Sign all dynamic libraries, helpers, and managed .NET assemblies first
  find "$APP_BUNDLE/Contents/MacOS" -type f \( -name "*.dylib" -o -name "*.so" -o -name "createdump" -o -name "*.dll" \) | while IFS= read -r f; do
    codesign --force --sign - "$f"
  done

  # 5b. Sign main executable with entitlements and runtime options
  ENTITLEMENTS_ARGS=()
  if [ -f "$SCRIPT_DIR/Entitlements.plist" ]; then
    ENTITLEMENTS_ARGS=(--entitlements "$SCRIPT_DIR/Entitlements.plist")
  fi

  echo "Signing main executable Anarise with entitlements..."
  codesign --force --options runtime "${ENTITLEMENTS_ARGS[@]}" --sign - "$APP_BUNDLE/Contents/MacOS/Anarise"

  # 5c. Sign the root application bundle container
  echo "Signing root bundle Anarise.app..."
  codesign --force --options runtime "${ENTITLEMENTS_ARGS[@]}" --sign - "$APP_BUNDLE"

  echo "Verifying code signature of Anarise.app..."
  codesign --verify --deep --strict --verbose=2 "$APP_BUNDLE"
fi

echo "ANARISE VPN successfully packaged into: $APP_BUNDLE"

# 6. Create DMG installer with Applications symlink and clear instruction file
DMG_STAGING="$SCRIPT_DIR/dmg-staging-$ARCH"
DMG_OUTPUT="$SCRIPT_DIR/AnariseVPN-macOS-$ARCH.dmg"

rm -rf "$DMG_STAGING" "$DMG_OUTPUT"
mkdir -p "$DMG_STAGING"

if command -v ditto >/dev/null 2>&1; then
  ditto "$APP_BUNDLE" "$DMG_STAGING/Anarise.app"
else
  cp -a "$APP_BUNDLE" "$DMG_STAGING/"
fi
ln -s /Applications "$DMG_STAGING/Applications"

cat > "$DMG_STAGING/КАК ОТКРЫТЬ ANARISE.txt" << 'EOF'
ИНСТРУКЦИЯ ПО УСТАНОВКЕ И ЗАПУСКУ ANARISE VPN (macOS):
======================================================

1. ПЕРЕТАЩИТЕ Anarise в папку «Applications» (Программы).

2. ПЕРВЫЙ ЗАПУСК (Защита macOS Gatekeeper):
   Так как ANARISE VPN — бесплатное приложение с открытым исходным кодом без платной подписки Apple Developer,
   macOS при первом открытии может показать стандартное системное предупреждение.

   ЕСТЬ 2 ПРОСТЫХ СПОСОБА ЕГО ОТКРЫТЬ:

   СПОСОБ 1 (через Настройки Mac — без Терминала):
   1. Запустите Anarise из папки «Программы». При появлении системного окна нажмите «Готово» (или «Отмена»).
   2. Откройте «Системные настройки» -> «Конфиденциальность и безопасность».
   3. Прокрутите вниз до раздела «Безопасность». Там появится кнопка:
      «Всё равно открыть» (Open Anyway). Нажмите её и подтвердите паролем.

   СПОСОБ 2 (через Терминал — за 2 секунды):
   Откройте программу «Терминал» и выполните команду:
   xattr -cr /Applications/Anarise.app

   После этого приложение открывается обычным двойным кликом!
======================================================
EOF

if command -v hdiutil >/dev/null 2>&1; then
  echo "Creating DMG image: $DMG_OUTPUT..."
  hdiutil create -volname "ANARISE VPN" -srcfolder "$DMG_STAGING" -ov -format UDZO "$DMG_OUTPUT"
  rm -rf "$DMG_STAGING"
  echo "DMG successfully created: $DMG_OUTPUT"
else
  echo "hdiutil not available in current environment; skipping DMG generation."
fi
