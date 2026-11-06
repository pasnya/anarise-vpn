#!/bin/bash
set -e

ARCH="${1:-osx-arm64}"
echo "Building ANARISE VPN for macOS architecture: $ARCH..."

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
ROOT_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"

PUBLISH_DIR="$SCRIPT_DIR/bin/Release/net8.0/$ARCH/publish"
APP_BUNDLE="$SCRIPT_DIR/Anarise.app"

# 1. Publish self-contained single-file .NET 8 binary so macOS users do not need .NET installed
dotnet publish "$SCRIPT_DIR/Anarise.Mac.csproj" \
  -c Release \
  -r "$ARCH" \
  --self-contained true \
  -p:PublishSingleFile=true \
  -o "$PUBLISH_DIR"

if [ ! -f "$PUBLISH_DIR/Anarise" ]; then
  echo "ERROR: self-contained single-file publish failed: missing Anarise executable in $PUBLISH_DIR" >&2
  exit 1
fi

# 2. Assemble .app bundle
rm -rf "$APP_BUNDLE"
mkdir -p "$APP_BUNDLE/Contents/MacOS"
mkdir -p "$APP_BUNDLE/Contents/Resources"

cp "$SCRIPT_DIR/Info.plist" "$APP_BUNDLE/Contents/Info.plist"
cp -R "$PUBLISH_DIR/"* "$APP_BUNDLE/Contents/MacOS/"

# Clean up debug symbols from release bundle
rm -f "$APP_BUNDLE/Contents/MacOS/"*.pdb

# Move non-executable assets/resources to Contents/Resources/
if [ -d "$APP_BUNDLE/Contents/MacOS/wwwroot" ]; then
  mv "$APP_BUNDLE/Contents/MacOS/wwwroot" "$APP_BUNDLE/Contents/Resources/"
fi
for res_file in external-sources.json trusted-binaries.json; do
  if [ -f "$APP_BUNDLE/Contents/MacOS/$res_file" ]; then
    mv "$APP_BUNDLE/Contents/MacOS/$res_file" "$APP_BUNDLE/Contents/Resources/"
  fi
done

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

# 4. Set clean permissions: directories 755, files 644, executable 755
find "$APP_BUNDLE" -type d -exec chmod 755 {} \;
find "$APP_BUNDLE" -type f -exec chmod 644 {} \;
chmod 755 "$APP_BUNDLE/Contents/MacOS/Anarise"

if command -v xattr >/dev/null 2>&1; then
  xattr -cr "$APP_BUNDLE" || true
fi

# 5. Ad-hoc code signing for macOS Apple Silicon & Intel
if command -v codesign >/dev/null 2>&1; then
  echo "Ad-hoc signing Mach-O binaries and Anarise.app bundle..."

  # 5a. Sign native dynamic libraries first
  find "$APP_BUNDLE/Contents/MacOS" -type f \( -name "*.dylib" -o -name "*.so" \) | while IFS= read -r f; do
    codesign --force --sign - "$f" || true
  done

  # 5b. Sign the root application bundle with Entitlements
  ENTITLEMENTS_ARGS=()
  if [ -f "$SCRIPT_DIR/Entitlements.plist" ]; then
    ENTITLEMENTS_ARGS=(--entitlements "$SCRIPT_DIR/Entitlements.plist")
  fi

  echo "Signing root bundle Anarise.app with entitlements..."
  codesign --force --deep "${ENTITLEMENTS_ARGS[@]}" --sign - "$APP_BUNDLE"

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
   macOS при первом открытии может показать системное окно:
   «Не удается открыть программу "Anarise.app"» (или предупреждение безопасности).

   ЕСТЬ 2 ПРОСТЫХ СПОСОБА ЕГО ОТКРЫТЬ:

   СПОСОБ 1 (через Настройки Mac — без Терминала):
   1. Запустите Anarise из папки «Программы». При появлении системного окна нажмите «Готово» (или «Отмена»).
   2. Откройте «Системные настройки» -> «Конфиденциальность и безопасность».
   3. Прокрутите вниз до раздела «Безопасность». Там появится кнопка:
      «Всё равно открыть» (Open Anyway). Нажмите её и подтвердите паролем Mac.

   СПОСОБ 2 (через Терминал — за 2 секунды):
   Откройте программу «Терминал» и выполните команду:
   xattr -cr /Applications/Anarise.app

   После этого приложение открывается обычным двойным кликом!
======================================================
EOF

if command -v hdiutil >/dev/null 2>&1; then
  echo "Creating DMG image: $DMG_OUTPUT..."
  SRC_SIZE_MB=$(du -sm "$DMG_STAGING" | awk '{print $1}')
  DMG_SIZE_MB=$((SRC_SIZE_MB + 100))
  echo "Staging size: ${SRC_SIZE_MB}MB, allocated DMG capacity: ${DMG_SIZE_MB}MB..."
  hdiutil create -size "${DMG_SIZE_MB}m" -volname "ANARISE VPN" -srcfolder "$DMG_STAGING" -ov -format UDZO "$DMG_OUTPUT"
  rm -rf "$DMG_STAGING"
  echo "DMG successfully created: $DMG_OUTPUT"
else
  echo "hdiutil not available in current environment; skipping DMG generation."
fi
