# ANARISE VPN v1.4.14

## Что нового

- Безопасная загрузка Windows-ядер по закреплённому manifest и SHA-256.
- Защищённое хранение профилей и VPN-ссылок: DPAPI на Windows, Android Keystore на Android.
- Восстановление системного proxy и служебных сетевых правил после сбоя приложения.
- Реальные TUN-метрики трафика и watchdog VPN-ядер.
- Автопереподключение после аварии ядра: до 3 попыток с задержками 2/4/8 секунд.
- Windows lifecycle ядра вынесен в `CoreProcessManager`.

## Артефакты

- `AnariseVPN-Windows-x64-v1.4.14.zip` — Windows x64, framework-dependent publish; требуется .NET 8 Desktop Runtime и WebView2 Runtime.
- `AnariseVPN-Android-debug-v1.4.14.apk` — debug APK для тестирования, не production-подпись.

## Проверки

- Windows publish: успешно.
- Android `assembleDebug`: успешно.
- Parser smoke tests: 6/6.

SHA-256 опубликован отдельным файлом `SHA256SUMS.txt`.
