# ANARISE VPN — сборка и базовые проверки

## Зафиксированное состояние

- Репозиторий: `pasnya/anarise-vpn`.
- Ветка: `main`.
- Базовый commit: `a4f2e76fb0e06355f29d680748cbcbfb2a770e2e`.
- Windows target: `net8.0-windows`, WPF, WebView2 `1.0.3967.48`.
- Android: compile/target SDK `36`, Java/Kotlin toolchain `17`, Gradle wrapper `9.1.0`.
- Android release: `arm64-v8a`, `minSdk 24`, `versionName 1.4.13`.

## Локальные команды

### Windows приложение

```powershell
dotnet build windows/Anarise.csproj --configuration Release --nologo
dotnet publish windows/Anarise.csproj --configuration Release --runtime win-x64 --self-contained false
```

### Parser smoke-тесты

```powershell
dotnet run --project tests/LinkParserHarness/LinkParserHarness.csproj --configuration Release -- --smoke
```

Тесты используют только синтетические конфигурации и не подключаются к реальным серверам.

### Манифест доверенных бинарников Windows

```powershell
dotnet run --project tests/LinkParserHarness/LinkParserHarness.csproj --configuration Release -- --validate-manifest
```

Проверяются HTTPS-адреса и SHA-256 для Xray, Hysteria, Mieru, tun2socks и WinTun.

### Проверка безопасного хранения

- Windows: `settings.json` и `history.json` шифруются через DPAPI (`CurrentUser`); старые открытые JSON мигрируют при запуске.
- Android: история и последний VPN-конфиг шифруются AES-GCM ключом из Android Keystore; старые SharedPreferences мигрируют при первом чтении.
- Рабочие JSON-конфиги ядер Windows удаляются после остановки VPN и при старте после аварийного завершения.

### Проверка восстановления сети

- До изменения системного proxy создаётся `network-recovery.json` в каталоге приложения.
- При штатном отключении, ошибке запуска, закрытии окна или следующем старте исходный proxy восстанавливается.
- Служебные правила блокировки IPv6 и QUIC удаляются при восстановлении.

### Метрики и watchdog

- Windows в TUN-режиме берёт байты из IPv4-счётчиков адаптера `anarise`, а не генерирует значения случайно.
- Watchdog раз в секунду проверяет состояние ядра и `tun2socks`.
- При аварии включается не более трёх попыток переподключения с задержками 2/4/8 секунд.
- Android получает трафик от `TProxyGetStats()` SDK через broadcast-события.

### Сервисные тесты Windows

```powershell
dotnet run --project tests/LinkParserHarness/LinkParserHarness.csproj --configuration Release -- --service-tests
```

Проверяется независимый расчёт дельты трафика, используемый UI статистики.

Управление процессом ядра вынесено в `CoreProcessManager`; его запуск и остановка требуют отдельного ручного прогона на Windows 10/11.

### Android приложение

Требуются Android SDK и Java 17:

```powershell
cd android
./gradlew.bat assembleRelease
```

APK должен появиться в `android/app/build/outputs/apk/release/app-release.apk`.

## Базовая матрица тестирования

| Область | Минимальная проверка | Статус этапа 0 |
|---|---|---|
| Windows компиляция | `dotnet build` | Пройдено |
| Parser: VLESS | Разбор и проверка протокола/порта | Пройдено |
| Parser: VMess | Base64 JSON и проверка протокола/порта | Пройдено |
| Parser: NaiveProxy | HTTPS-ссылка и параметры TLS | Пройдено |
| Parser: Hysteria2 | `hysteria2://` | Пройдено |
| Parser: Hysteria2 alias | `hy2://` | Пройдено |
| Parser: Mieru | URI-ссылка | Пройдено |
| Android компиляция | `gradlew.bat assembleRelease` | Требует настроенный Android SDK |
| Реальное VPN-подключение | Тестовый сервер каждого протокола | Отдельный этап |
| Windows 10/11 | Proxy и TUN-режим | Отдельный ручной прогон |
| Android 7–15 | VPN permission, background, reboot | Отдельный ручной прогон |

## Контрольные ограничения

1. Тесты не должны содержать реальные VPN-ссылки, пароли, токены или адреса рабочих серверов.
2. Smoke-тесты проверяют только парсинг и структуру результата; они не доказывают доступность сервера.
3. Перед релизом необходимо отдельно проверить IPv4/IPv6, потерю сети, аварийное завершение ядра и восстановление системных настроек.
4. Release Android требует переменных `ANARISE_STORE_FILE`, `ANARISE_STORE_PASSWORD`, `ANARISE_KEY_ALIAS` и `ANARISE_KEY_PASSWORD`.

## Известные baseline-проблемы

- Windows-сборка проходит без ошибок, но выдает предупреждения nullable-контекста; их устранение относится к последующим этапам.
- Android-сборка в текущем окружении не запускалась: Android SDK не настроен.
- Smoke-тесты пока находятся в отдельном console harness и не подключены к полноценному test runner.
