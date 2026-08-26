# ANARISE VPN v1.4.15

## Что нового

- Исправлена ошибка первого запуска Windows: загрузка ядер больше не прерывается стандартным таймаутом 100 секунд.
- Таймаут загрузки Windows-модулей увеличен до 15 минут для медленных CDN, VPN и прокси-соединений.
- Для загрузки ядер добавлен корректный User-Agent и автоматическая обработка HTTP-сжатия.
- В Windows Server Explorer добавлены поиск и фильтрация серверов по регионам.
- В Android обновлён интерфейс Server Explorer в стиле варианта C: Global Network, поиск и региональные фильтры.
- Проверка SHA-256 и безопасная атомарная установка загруженных файлов сохранены.

## Артефакты

- `AnariseVPN-Windows-x64-v1.4.15.zip` — Windows x64, framework-dependent publish; требуется .NET 8 Desktop Runtime и WebView2 Runtime.
- `AnariseVPN-Android-debug-v1.4.15.apk` — debug APK для тестирования, не production-подпись.

## Проверки

- Windows `dotnet publish`: успешно.
- Android `assembleDebug`: успешно.
- JavaScript WebView2 frontend: синтаксис проверен.

SHA-256 опубликован в файле `SHA256SUMS.txt`.
