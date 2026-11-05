using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Photino.NET;

namespace Anarise;

public static class Program
{
    private static PhotinoWindow? window;
    private static readonly MacAppController Controller = new();

    [STAThread]
    public static void Main(string[] args)
    {
        string appDir = AppDomain.CurrentDomain.BaseDirectory;
        try { Directory.SetCurrentDirectory(appDir); } catch { }

        MacNetworkStateGuard.RecoverPreviousSessionIfNeeded();

        string wwwroot = Path.Combine(appDir, "wwwroot");

        window = new PhotinoWindow()
            .SetTitle("ANARISE VPN")
            .SetSize(520, 680)
            .SetMinSize(420, 500)
            .SetResizable(true)
            .RegisterWebMessageReceivedHandler((sender, message) =>
            {
                Controller.HandleWebMessage(message);
            })
            .RegisterWindowClosingHandler((sender, e) =>
            {
                Controller.OnClosing();
                return false;
            });

        Controller.AttachWindow(window);

        string indexPath = Path.Combine(wwwroot, "index.html");
        if (File.Exists(indexPath))
        {
            window.Load(indexPath);
        }
        else
        {
            window.LoadRawString("<html><body style='background:#111;color:#fff;'><h3>Error: wwwroot/index.html not found</h3></body></html>");
        }

        window.WaitForClose();
    }
}

public sealed class MacAppController
{
    private PhotinoWindow? window;
    private Process? coreProcess;
    private Process? routingProcess;
    private Process? tun2socksProcess;
    private readonly CoreProcessManager coreManager = new();
    private readonly CoreProcessManager routingManager = new();
    private readonly StatisticsService statsService = new();
    private readonly string appDataDir;
    private readonly string settingsPath;
    private readonly string historyPath;
    private readonly string externalCachePath;
    private readonly string binariesDir;

    private readonly List<string> configHistory = new();
    private string currentConfigLink = "";
    private bool killSwitch = false;
    private bool autostart = false;
    private bool autoreconnect = true;
    private bool bypassLan = true;
    private bool systemProxy = true;
    private bool vpnMode = false;
    private int socksPort = 20808;
    private int httpPort = 20809;
    private bool proxyAuthEnabled = true;
    private bool activeEnableHttpProxy = true;
    private string geoRoutingRules = "";
    private int upstreamSocksPort;
    private int upstreamHttpPort;
    private string proxyUsername = LocalProxyCredentials.Create(10);
    private string proxyPassword = LocalProxyCredentials.Create();
    private MacProxySettingsSnapshot? originalProxySettings;
    private string[]? originalDnsServers;

    private bool isConnected = false;
    private string currentState = "DISCONNECTED";
    private DateTime connectionStartTime = DateTime.UtcNow;
    private double totalUploadBytes = 0;
    private double totalDownloadBytes = 0;
    private double currentUploadSpeed = 0;
    private double currentDownloadSpeed = 0;

    private int connectionGeneration = 0;
    private string? savedServerIp;
    private string? savedDefaultGateway;
    private string? activePhysicalInterfaceName;
    private bool tunRoutesActive;

    private bool externalScanInProgress;
    private CancellationTokenSource? externalScanCts;
    private CancellationTokenSource? reconnectCts;
    private int reconnectAttempts;
    private bool reconnectInProgress;
    private System.Threading.Timer? statsTimer;
    private Task? binariesSetupTask;
    private bool updateChecked;

    private const string AppVersion = "1.4.20";
    private const string RequiredXrayVersion = "26.7.11";
    private const int ExternalConfigBatchSize = 200;
    private const int ExternalTrafficBatchSize = 30;
    private static readonly TimeSpan BinaryDownloadTimeout = TimeSpan.FromMinutes(15);

    private readonly SemaphoreSlim validationSlots = new(3, 3);

    public MacAppController()
    {
        appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".anarise");
        Directory.CreateDirectory(appDataDir);
        settingsPath = Path.Combine(appDataDir, "settings.json");
        historyPath = Path.Combine(appDataDir, "history.json");
        externalCachePath = Path.Combine(appDataDir, "external_cache.json");
        binariesDir = Path.Combine(appDataDir, "bin");
        Directory.CreateDirectory(binariesDir);

        KillOrphanedProcesses();
        CleanupRuntimeConfigFiles();
        LoadSettings();
        LoadHistory();
        SaveSettings();
    }

    public void AttachWindow(PhotinoWindow win)
    {
        window = win;
    }

    public async void HandleWebMessage(string message)
    {
        try
        {
            var node = JsonNode.Parse(message);
            string? action = node?["action"]?.ToString();
            if (string.IsNullOrEmpty(action)) return;

            switch (action)
            {
                case "appReady":
                    SendSettingsAndHistory();
                    binariesSetupTask ??= CheckAndDownloadBinariesAsync();
                    await binariesSetupTask;
                    CheckForUpdates();
                    break;

                case "pasteFromClipboard":
                    PasteFromClipboard();
                    break;

                case "importSubscription":
                    string? subUrl = node?["url"]?.ToString();
                    if (!string.IsNullOrEmpty(subUrl))
                    {
                        await ImportSubscriptionAsync(subUrl);
                    }
                    break;

                case "connect":
                    await ConnectAsync();
                    break;

                case "disconnect":
                    Disconnect(preserveKillSwitch: false);
                    break;

                case "selectConfig":
                    currentConfigLink = node?["link"]?.ToString() ?? "";
                    SaveSettings();
                    PostToUi(new { action = "updateSelectedConfig", link = currentConfigLink });
                    break;

                case "deleteConfig":
                    string? toDelete = node?["link"]?.ToString();
                    if (!string.IsNullOrEmpty(toDelete))
                    {
                        configHistory.Remove(toDelete);
                        if (currentConfigLink == toDelete)
                        {
                            currentConfigLink = configHistory.FirstOrDefault() ?? "";
                            SaveSettings();
                            PostToUi(new { action = "updateSelectedConfig", link = currentConfigLink });
                        }
                        SaveHistory();
                        PostToUi(new { action = "updateHistory", history = configHistory });
                    }
                    break;

                case "saveSetting":
                    string? name = node?["name"]?.ToString();
                    var valNode = node?["value"];
                    if (!string.IsNullOrEmpty(name) && valNode != null)
                    {
                        UpdateSetting(name, valNode);
                    }
                    break;

                case "checkServerPing":
                    string? linkToPing = node?["link"]?.ToString();
                    if (!string.IsNullOrEmpty(linkToPing))
                    {
                        _ = CheckPingAsync(linkToPing);
                    }
                    break;

                case "checkAllPings":
                    _ = CheckAllPingsAsync();
                    break;

                case "refreshExternal":
                    _ = ScanExternalSourcesAsync();
                    break;

                case "cancelExternal":
                    externalScanCts?.Cancel();
                    PostToUi(new { action = "updateExternalScanning", scanning = false });
                    break;

                case "copyToClipboard":
                    string? copyText = node?["text"]?.ToString();
                    if (!string.IsNullOrEmpty(copyText))
                    {
                        if (MacProxyManager.WriteClipboardText(copyText))
                        {
                            PostToUi(new { action = "showToast", message = "Скопировано в буфер обмена" });
                        }
                    }
                    break;

                case "openBrowser":
                    string? urlToOpen = node?["url"]?.ToString();
                    if (!string.IsNullOrWhiteSpace(urlToOpen))
                    {
                        try
                        {
                            Process.Start(new ProcessStartInfo
                            {
                                FileName = "open",
                                Arguments = $"\"{urlToOpen}\"",
                                UseShellExecute = false
                            });
                        }
                        catch (Exception ex)
                        {
                            LogToUi("Ошибка открытия браузера: " + ex.Message);
                        }
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            LogToUi($"Ошибка обработки команды UI: {ex.Message}");
        }
    }

    private void PostToUi(object payload)
    {
        try
        {
            string json = JsonSerializer.Serialize(payload);
            window?.SendWebMessage(json);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MacAppController] PostToUi error: {ex.Message}");
        }
    }

    private void LogToUi(string message)
    {
        string cleanMsg = message.Replace("\n", " ").Replace("\r", " ").Trim();
        if (string.IsNullOrEmpty(cleanMsg)) return;
        string logLine = $"[{DateTime.Now:HH:mm:ss}] {cleanMsg}";
        PostToUi(new { action = "addLog", log = logLine });
    }

    private void SendSettingsAndHistory()
    {
        PostToUi(new
        {
            action = "updateSettings",
            settings = new
            {
                killSwitch,
                autostart,
                autoreconnect,
                bypassLan,
                socksPort,
                httpPort,
                proxyAuthEnabled,
                proxyUsername,
                proxyPassword,
                geoRoutingRules,
                vpnMode,
                systemProxy
            }
        });
        PostToUi(new { action = "updateHistory", history = configHistory });
        PostToUi(new { action = "updateSelectedConfig", link = currentConfigLink });

        if (File.Exists(externalCachePath))
        {
            try
            {
                var cache = JsonNode.Parse(File.ReadAllText(externalCachePath));
                var checkedAt = cache?["checkedAt"]?.GetValue<DateTimeOffset>();
                if (checkedAt.HasValue && DateTimeOffset.UtcNow - checkedAt.Value < TimeSpan.FromMinutes(15))
                {
                    PostToUi(new { action = "updateExternalConfigs", configs = cache!["configs"], checkedAt });
                    PostToUi(new { action = "updateExternalStatus", statusText = "Сохранённая проверка: " + checkedAt.Value.ToLocalTime().ToString("HH:mm") });
                }
            }
            catch { }
        }

        PostToUi(new { action = "updateState", state = isConnected ? "CONNECTED" : "DISCONNECTED" });
    }

    // --- CONNECTION MANAGEMENT ---
    private async Task ConnectAsync()
    {
        binariesSetupTask ??= CheckAndDownloadBinariesAsync();
        await binariesSetupTask;

        if (string.IsNullOrWhiteSpace(currentConfigLink))
        {
            PostToUi(new { action = "showToast", message = "Пожалуйста, выберите или добавьте сервер!" });
            return;
        }

        int gen = Interlocked.Increment(ref connectionGeneration);

        currentState = "CONNECTING";
        PostToUi(new { action = "updateState", state = currentState });
        LogToUi("Инициализация подключения...");

        try
        {
            StopTun2Socks();
            StopTunnelCore();
            CleanupRuntimeConfigFiles();
            MacProxyManager.SetKillSwitchBlocked(false);
            KillOrphanedProcesses();

            if (!EnsureLocalPortsAvailable())
            {
                LogToUi("Не удалось найти свободные локальные порты для прокси.");
                if (gen == connectionGeneration) PostToUi(new { action = "updateState", state = "ERROR" });
                return;
            }

            if (gen != connectionGeneration) return;

            string parsedConfig;
            try
            {
                parsedConfig = LinkParser.Parse(currentConfigLink, socksPort, httpPort);
            }
            catch (Exception ex)
            {
                LogToUi("Ошибка парсинга конфигурации: " + ex.Message);
                if (gen == connectionGeneration) PostToUi(new { action = "updateState", state = "ERROR" });
                return;
            }

            bool isHysteria = LinkParser.IsHysteria2Config(parsedConfig);
            bool isMieru = LinkParser.IsMieruConfig(parsedConfig);
            IReadOnlyList<GeoRoutingRule> configuredGeoRules;
            try { configuredGeoRules = GeoRouting.Parse(geoRoutingRules); }
            catch (FormatException ex)
            {
                LogToUi("Ошибка geo-маршрутизации: " + ex.Message);
                if (gen == connectionGeneration) PostToUi(new { action = "updateState", state = "ERROR" });
                return;
            }
            bool requiresRoutingCore = configuredGeoRules.Count > 0 && (isHysteria || isMieru);
            if (configuredGeoRules.Count > 0)
                LogToUi($"Загружено geo-правил: {configuredGeoRules.Count}.");
            activeEnableHttpProxy = !isMieru || requiresRoutingCore;
            string coreBinaryName = isMieru ? "mieru" : isHysteria ? "hysteria" : "xray";
            string coreBinaryPath = Path.Combine(binariesDir, coreBinaryName);

            if (!File.Exists(coreBinaryPath))
            {
                LogToUi($"Модуль ядра {coreBinaryName} не найден по пути {coreBinaryPath}.");
                if (gen == connectionGeneration) PostToUi(new { action = "updateState", state = "ERROR" });
                return;
            }

            string xrayPath = Path.Combine(binariesDir, "xray");
            if (requiresRoutingCore && !File.Exists(xrayPath))
            {
                LogToUi("Для geo-маршрутизации Hysteria2/Mieru не найден Xray.");
                if (gen == connectionGeneration) PostToUi(new { action = "updateState", state = "ERROR" });
                return;
            }

            // Resolve server domain via DoH
            if (isMieru)
            {
                var rawMieruConfig = JsonNode.Parse(parsedConfig)!.AsObject();
                string? host = rawMieruConfig["server_host"]?.ToString();
                string? port = rawMieruConfig["server_port"]?.ToString();
                if (!string.IsNullOrEmpty(host))
                {
                    string resolvedIp = await ResolveDohAsync(host);
                    if (gen != connectionGeneration) return;
                    rawMieruConfig["server"] = $"{resolvedIp}:{port}";
                    rawMieruConfig["server_host"] = resolvedIp;
                    parsedConfig = rawMieruConfig.ToJsonString();
                }
            }
            else if (isHysteria)
            {
                var rawHysteriaConfig = JsonNode.Parse(parsedConfig)!.AsObject();
                string? host = rawHysteriaConfig["server_host"]?.ToString();
                string? port = rawHysteriaConfig["server_port"]?.ToString();
                if (!string.IsNullOrEmpty(host))
                {
                    string resolvedIp = await ResolveDohAsync(host);
                    if (gen != connectionGeneration) return;
                    rawHysteriaConfig["server"] = $"{resolvedIp}:{port}";
                    rawHysteriaConfig["server_host"] = resolvedIp;
                    if (rawHysteriaConfig["sni"] == null || string.IsNullOrEmpty(rawHysteriaConfig["sni"]!.ToString()))
                    {
                        rawHysteriaConfig["sni"] = host;
                    }
                    parsedConfig = rawHysteriaConfig.ToJsonString();
                }
            }
            else
            {
                var xrayConfig = JsonNode.Parse(parsedConfig)!.AsObject();
                if (xrayConfig["log"] == null) xrayConfig["log"] = new JsonObject();
                xrayConfig["log"]!.AsObject()["loglevel"] = "warning";

                var outbounds = xrayConfig["outbounds"]?.AsArray();
                if (outbounds != null && outbounds.Count > 0)
                {
                    var settings = outbounds[0]?["settings"]?.AsObject();
                    if (settings != null)
                    {
                        if (settings["vnext"] != null && settings["vnext"]!.AsArray().Count > 0)
                        {
                            var vnextObj = settings["vnext"]?[0]?.AsObject();
                            string? host = vnextObj?["address"]?.ToString();
                            if (vnextObj != null && !string.IsNullOrEmpty(host))
                            {
                                string resolvedIp = await ResolveDohAsync(host);
                                if (gen != connectionGeneration) return;
                                vnextObj["address"] = resolvedIp;
                            }
                        }
                        else if (settings["servers"] != null && settings["servers"]!.AsArray().Count > 0)
                        {
                            var serverObj = settings["servers"]?[0]?.AsObject();
                            string? host = serverObj?["address"]?.ToString();
                            if (serverObj != null && !string.IsNullOrEmpty(host))
                            {
                                string resolvedIp = await ResolveDohAsync(host);
                                if (gen != connectionGeneration) return;
                                serverObj["address"] = resolvedIp;
                            }
                        }
                    }
                }
                parsedConfig = xrayConfig.ToJsonString();
            }

            if (gen != connectionGeneration) return;

            savedServerIp = DesktopCoreServices.ExtractServerIp(parsedConfig);
            var (defaultIface, defaultGw) = MacProxyManager.GetDefaultRouteInfo();
            activePhysicalInterfaceName = defaultIface;
            savedDefaultGateway = defaultGw;

            string configFilePath = Path.Combine(appDataDir,
                isMieru ? "mieru_config.json" : isHysteria ? "hysteria_config.json" : "xray_config.json");
            string finalCoreConfig;
            if (requiresRoutingCore)
            {
                (upstreamSocksPort, upstreamHttpPort) = FindRoutingUpstreamPorts();
                finalCoreConfig = DesktopCoreServices.BuildRuntimeCoreConfig(parsedConfig, upstreamSocksPort, upstreamHttpPort, isMieru, isHysteria);
            }
            else
            {
                finalCoreConfig = DesktopCoreServices.BuildRuntimeCoreConfig(parsedConfig, socksPort, httpPort, isMieru, isHysteria,
                    proxyAuthEnabled ? proxyUsername : null, proxyAuthEnabled ? proxyPassword : null);
                if (!isHysteria && !isMieru)
                    finalCoreConfig = GeoRouting.ApplyToXrayConfig(finalCoreConfig, geoRoutingRules,
                        vpnMode ? activePhysicalInterfaceName : null);
            }
            File.WriteAllText(configFilePath, finalCoreConfig);

            if (gen != connectionGeneration) return;

            var startInfo = new ProcessStartInfo
            {
                FileName = coreBinaryPath,
                Arguments = isMieru ? "run" : isHysteria ? $"-c \"{configFilePath}\" client" : $"-c \"{configFilePath}\"",
                WorkingDirectory = binariesDir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            startInfo.Environment["GOMEMLIMIT"] = "50MiB";
            startInfo.Environment["GOGC"] = "30";
            if (isMieru)
            {
                startInfo.Environment["MIERU_CONFIG_JSON_FILE"] = configFilePath;
            }

            LogToUi($"Запуск ядра: {coreBinaryName}...");
            coreProcess = coreManager.Start(startInfo, LogToUi, LogToUi);

            await Task.Delay(1500);
            if (gen != connectionGeneration) return;

            if (coreProcess.HasExited)
            {
                LogToUi("Процесс ядра неожиданно завершился.");
                PostToUi(new { action = "updateState", state = "ERROR" });
                return;
            }

            if (requiresRoutingCore)
            {
                string routingConfigPath = Path.Combine(appDataDir, "geo_router_config.json");
                string routingConfig = GeoRouting.BuildRouterConfig(socksPort, httpPort, upstreamSocksPort,
                    proxyAuthEnabled ? proxyUsername : null, proxyAuthEnabled ? proxyPassword : null, geoRoutingRules, activePhysicalInterfaceName);
                File.WriteAllText(routingConfigPath, routingConfig);
                if (OperatingSystem.IsMacOS())
                    File.SetUnixFileMode(routingConfigPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
                var routerStartInfo = new ProcessStartInfo
                {
                    FileName = xrayPath,
                    Arguments = $"-c \"{routingConfigPath}\"",
                    WorkingDirectory = binariesDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };
                routerStartInfo.Environment["GOMEMLIMIT"] = "30MiB";
                routerStartInfo.Environment["GOGC"] = "30";
                routingProcess = routingManager.Start(routerStartInfo, LogToUi, LogToUi);
                await Task.Delay(1000);
                if (gen != connectionGeneration) return;
                if (routingProcess.HasExited)
                {
                    LogToUi("Общий geo-маршрутизатор Xray неожиданно завершился.");
                    StopTunnelCore();
                    if (gen == connectionGeneration) PostToUi(new { action = "updateState", state = "ERROR" });
                    return;
                }
                LogToUi("Общий geo-маршрутизатор активен для Hysteria2/Mieru.");
            }

            originalDnsServers ??= MacProxyManager.GetDnsServers();
            originalProxySettings ??= MacProxyManager.CaptureSettings();
            var originalDns = originalDnsServers;

            if (vpnMode)
            {
                LogToUi("Запуск VPN-туннеля (TUN)...");
                bool tunStarted = await StartTun2SocksAsync();
                if (gen != connectionGeneration) return;

                if (!tunStarted)
                {
                    StopTun2Socks();
                    LogToUi("Не удалось запустить VPN-туннель.");
                    StopTunnelCore();
                    PostToUi(new { action = "updateState", state = "ERROR" });
                    return;
                }
            }
            else if (systemProxy)
            {
                LogToUi("Настройка системного прокси macOS...");
                if (!MacNetworkStateGuard.RecordSession(true, killSwitch, MacProxyManager.GetActiveService(), originalDnsServers: originalDns,
                    originalProxySettings: originalProxySettings))
                    throw new IOException("Не удалось сохранить состояние сети для аварийного восстановления.");
                MacProxyManager.SetProxy(true, socksPort, httpPort, bypassLan, enableHttpProxy: activeEnableHttpProxy,
                    username: proxyAuthEnabled ? proxyUsername : null, password: proxyAuthEnabled ? proxyPassword : null);
            }

            if (!MacNetworkStateGuard.RecordSession(
                proxyActive: systemProxy && !vpnMode,
                killSwitchActive: killSwitch,
                service: MacProxyManager.GetActiveService(),
                tunRoutesActive: tunRoutesActive,
                serverIp: savedServerIp,
                originalDnsServers: originalDns,
                originalProxySettings: originalProxySettings))
                throw new IOException("Не удалось сохранить состояние активной сети.");

            isConnected = true;
            reconnectAttempts = 0;
            currentState = "CONNECTED";
            connectionStartTime = DateTime.UtcNow;
            totalUploadBytes = 0;
            totalDownloadBytes = 0;
            statsService.Reset();

            PostToUi(new { action = "updateState", state = currentState });
            PostToUi(new { action = "showToast", message = "Подключено!" });
            LogToUi("Подключение установлено успешно!");

            statsTimer?.Dispose();
            statsTimer = new System.Threading.Timer(_ => UpdateStatsTick(), null, 1000, 1000);

            _ = FetchExitIpInfoAsync();
        }
        catch (Exception ex)
        {
            currentState = "ERROR";
            LogToUi($"Ошибка подключения: {ex.Message}");
            Disconnect(preserveKillSwitch: false);
            if (gen == connectionGeneration) PostToUi(new { action = "updateState", state = "ERROR" });
        }
    }

    private void Disconnect(bool preserveKillSwitch)
    {
        Interlocked.Increment(ref connectionGeneration);
        if (!preserveKillSwitch)
        {
            reconnectCts?.Cancel();
            reconnectInProgress = false;
        }

        statsTimer?.Dispose();
        statsTimer = null;

        string? lastServerIp = savedServerIp;
        bool networkRestored = StopTun2Socks();
        StopTunnelCore();
        CleanupRuntimeConfigFiles();

        if (systemProxy)
        {
            if (!preserveKillSwitch && originalProxySettings != null)
                networkRestored &= MacProxyManager.RestoreSettings(originalProxySettings);
            else
                networkRestored &= MacProxyManager.SetProxy(false);
        }

        if (preserveKillSwitch && killSwitch)
        {
            if (MacProxyManager.SetKillSwitchBlocked(true, lastServerIp))
            {
                LogToUi("Kill Switch активирован: интернет заблокирован до восстановления VPN.");
            }
        }
        else
        {
            networkRestored &= MacProxyManager.SetKillSwitchBlocked(false);
            if (networkRestored)
            {
                MacNetworkStateGuard.ClearSession();
                originalProxySettings = null;
                originalDnsServers = null;
            }
            else LogToUi("Сетевые настройки восстановлены не полностью; сохранено состояние для восстановления при следующем запуске.");
        }

        currentState = "DISCONNECTED";
        PostToUi(new { action = "updateState", state = currentState });
        PostToUi(new { action = "updateStats", uploadSpeed = 0L, downloadSpeed = 0L, totalUpload = 0L, totalDownload = 0L, duration = 0L });
        LogToUi("Соединение разорвано.");
    }

    private void StopTunnelCore()
    {
        routingManager.Stop();
        routingProcess = null;
        coreManager.Stop();
        coreProcess = null;
        isConnected = false;
    }

    private (int SocksPort, int HttpPort) FindRoutingUpstreamPorts()
    {
        var excluded = new HashSet<int> { socksPort, httpPort, 20810 };
        int socks = FindFreePort(31000, requireUdp: true, excluded);
        excluded.Add(socks);
        int http = FindFreePort(socks + 1, requireUdp: false, excluded);
        return (socks, http);
    }

    private static int FindFreePort(int start, bool requireUdp, HashSet<int> excluded)
    {
        for (int offset = 0; offset < 2000; offset++)
        {
            int candidate = 31000 + ((start - 31000 + offset) % 30000);
            if (!excluded.Contains(candidate) && DesktopCoreServices.IsLocalPortAvailable(candidate, requireUdp))
                return candidate;
        }
        throw new InvalidOperationException("Не удалось найти свободный внутренний порт маршрутизатора.");
    }

    // --- TUN2SOCKS FOR MACOS ---
    private async Task<bool> StartTun2SocksAsync()
    {
        string tunExe = Path.Combine(binariesDir, "tun2socks");
        if (!File.Exists(tunExe))
        {
            LogToUi("Модуль tun2socks не найден.");
            return false;
        }

        try
        {
            const string utunName = "utun85";
            var psi = new ProcessStartInfo
            {
                FileName = tunExe,
                Arguments = $"-device tun://{utunName} -proxy socks5://{(proxyAuthEnabled ? Uri.EscapeDataString(proxyUsername) + ":" + Uri.EscapeDataString(proxyPassword) + "@" : "")}127.0.0.1:{socksPort} -loglevel warn",
                WorkingDirectory = binariesDir,
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.Environment["GOMEMLIMIT"] = "30MiB";
            psi.Environment["GOGC"] = "30";

            tun2socksProcess = new Process { StartInfo = psi, EnableRaisingEvents = true };
            tun2socksProcess.OutputDataReceived += (s, ev) => { if (ev.Data != null) LogToUi("[tun] " + ev.Data); };
            tun2socksProcess.ErrorDataReceived += (s, ev) => { if (ev.Data != null) LogToUi("[tun] " + ev.Data); };
            tun2socksProcess.Start();
            tun2socksProcess.BeginOutputReadLine();
            tun2socksProcess.BeginErrorReadLine();

            await Task.Delay(1500);
            if (tun2socksProcess.HasExited)
            {
                LogToUi("Для запуска TUN-интерфейса на macOS требуются права администратора (или используйте режим Системного прокси).");
                return false;
            }

            if (!MacNetworkStateGuard.RecordSession(false, killSwitch, MacProxyManager.GetActiveService(), tunRoutesActive: true,
                serverIp: savedServerIp, originalDnsServers: originalDnsServers, originalProxySettings: originalProxySettings))
                throw new IOException("Не удалось сохранить состояние TUN для восстановления.");
            tunRoutesActive = true;
            if (!MacProxyManager.ConfigureTunRoutes(utunName, savedServerIp, savedDefaultGateway))
            {
                LogToUi("Не удалось настроить маршруты TUN-интерфейса.");
                return false;
            }

            LogToUi("VPN-туннель (utun85) активирован.");
            return true;
        }
        catch (Exception ex)
        {
            LogToUi("Ошибка запуска tun2socks: " + ex.Message);
            return false;
        }
    }

    private bool StopTun2Socks()
    {
        var proc = tun2socksProcess;
        bool restored = true;
        try
        {
            if (tunRoutesActive)
            {
                restored = MacProxyManager.CleanupTunRoutes(savedServerIp);
                tunRoutesActive = false;
            }

            if (proc != null && !proc.HasExited)
            {
                proc.Kill(true);
                proc.WaitForExit(3000);
            }
        }
        catch { }
        finally
        {
            if (tun2socksProcess == proc) tun2socksProcess = null;
            proc?.Dispose();
            savedServerIp = null;
            savedDefaultGateway = null;
        }
        return restored;
    }

    // --- TRAFFIC STATISTICS & HEALTH MONITOR ---
    private void UpdateStatsTick()
    {
        if (!isConnected) return;

        if (!CoreHealthMonitor.IsHealthy(coreProcess, tun2socksProcess, vpnMode && tun2socksProcess != null) ||
            (routingProcess != null && routingProcess.HasExited))
        {
            LogToUi("VPN-ядро неожиданно завершилось.");
            Disconnect(preserveKillSwitch: true);
            ScheduleReconnect();
            return;
        }

        try
        {
            var allNics = NetworkInterface.GetAllNetworkInterfaces();
            var targetNic = allNics.FirstOrDefault(n =>
                n.Name.Equals("utun85", StringComparison.OrdinalIgnoreCase) &&
                n.OperationalStatus == OperationalStatus.Up);

            if (targetNic == null && !string.IsNullOrEmpty(activePhysicalInterfaceName))
            {
                targetNic = allNics.FirstOrDefault(n =>
                    string.Equals(n.Name, activePhysicalInterfaceName, StringComparison.OrdinalIgnoreCase) &&
                    n.OperationalStatus == OperationalStatus.Up);
            }

            if (targetNic != null)
            {
                var stats = targetNic.GetIPStatistics();
                var snapshot = statsService.Update(stats.BytesSent, stats.BytesReceived);
                currentUploadSpeed = snapshot.UploadSpeed;
                currentDownloadSpeed = snapshot.DownloadSpeed;
            }
            else
            {
                currentUploadSpeed = 0;
                currentDownloadSpeed = 0;
            }
        }
        catch
        {
            currentUploadSpeed = 0;
            currentDownloadSpeed = 0;
        }

        totalUploadBytes += currentUploadSpeed;
        totalDownloadBytes += currentDownloadSpeed;
        var duration = (long)(DateTime.UtcNow - connectionStartTime).TotalSeconds;

        PostToUi(new
        {
            action = "updateStats",
            uploadSpeed = (long)currentUploadSpeed,
            downloadSpeed = (long)currentDownloadSpeed,
            totalUpload = (long)totalUploadBytes,
            totalDownload = (long)totalDownloadBytes,
            duration
        });
    }

    private void ScheduleReconnect()
    {
        if (!autoreconnect || reconnectInProgress || string.IsNullOrWhiteSpace(currentConfigLink)) return;
        if (reconnectAttempts >= 3)
        {
            LogToUi("Автопереподключение остановлено после 3 попыток.");
            return;
        }

        reconnectAttempts++;
        reconnectInProgress = true;
        reconnectCts?.Cancel();
        reconnectCts = new CancellationTokenSource();
        var token = reconnectCts.Token;
        int delaySeconds = (int)Math.Pow(2, reconnectAttempts);

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), token);
                reconnectInProgress = false;
                if (!token.IsCancellationRequested && !isConnected)
                {
                    await ConnectAsync();
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { LogToUi("Ошибка автопереподключения: " + ex.Message); }
            finally { reconnectInProgress = false; }
        });
        LogToUi($"Автопереподключение через {delaySeconds} с (попытка {reconnectAttempts}/3).");
    }

    // --- EXIT IP CHECKER ---
    private async Task FetchExitIpInfoAsync()
    {
        var exitIp = await DesktopCoreServices.FetchExitIpInfoAsync(socksPort, LogToUi,
            proxyAuthEnabled ? proxyUsername : null, proxyAuthEnabled ? proxyPassword : null);
        if (exitIp != null)
        {
            PostToUi(new { action = "updateExitIp", exitIp });
        }
    }

    // --- DOH RESOLUTION ---
    private Task<string> ResolveDohAsync(string hostname) =>
        DesktopCoreServices.ResolveDohAsync(hostname, LogToUi);

    // --- PING & EXTERNAL SCANNING ---
    private Task<long> MeasurePingAsync(string link) =>
        DesktopCoreServices.MeasureTcpPingAsync(link);

    private async Task<ServerProbeResult> VerifyProxyTrafficAsync(string link)
    {
        if (isConnected) return new(ProbeStatus.Busy);
        await validationSlots.WaitAsync();
        try
        {
            binariesSetupTask ??= CheckAndDownloadBinariesAsync();
            await binariesSetupTask;
            if (isConnected) return new(ProbeStatus.Busy);
            return await new ServerProbe(binariesDir, appDataDir).CheckAsync(link);
        }
        finally
        {
            validationSlots.Release();
        }
    }

    private async Task CheckPingAsync(string link)
    {
        PostToUi(new { action = "updatePingLoading", link, loading = true });
        var result = await VerifyProxyTrafficAsync(link);
        PostToUi(new { action = "updatePing", link, latency = result.Latency, verificationStatus = result.Label });
    }

    private async Task CheckAllPingsAsync()
    {
        var tasks = configHistory.Select(CheckPingAsync).ToArray();
        await Task.WhenAll(tasks);
    }

    private async Task ScanExternalSourcesAsync()
    {
        if (externalScanInProgress) return;
        if (isConnected)
        {
            PostToUi(new { action = "updateExternalStatus", statusText = "Отключите VPN перед проверкой внешнего списка" });
            return;
        }

        externalScanInProgress = true;
        externalScanCts?.Cancel();
        externalScanCts = new CancellationTokenSource();
        var scanToken = externalScanCts.Token;

        try
        {
            PostToUi(new { action = "updateExternalScanning", scanning = true });
            PostToUi(new { action = "updateExternalConfigs", configs = Array.Empty<object>() });
            PostToUi(new { action = "updateExternalStatus", statusText = "Подготовка VPN-модулей..." });

            binariesSetupTask ??= CheckAndDownloadBinariesAsync();
            await binariesSetupTask;

            if (!new[] { "xray", "hysteria", "mieru" }.Any(name => File.Exists(Path.Combine(binariesDir, name))))
            {
                PostToUi(new { action = "updateExternalStatus", statusText = "Проверка недоступна: VPN-модули не установлены. Подробности в логах." });
                return;
            }

            PostToUi(new { action = "updateExternalStatus", statusText = "Загрузка свежих списков GitHub..." });
            string catalogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "external-sources.json");
            var sources = ExternalSources.Load(catalogPath);
            var loaded = await ExternalSourceLoader.FetchAsync(sources, LogToUi);
            var uniqueLinks = loaded.Links;

            if (uniqueLinks.Count == 0)
            {
                PostToUi(new { action = "updateExternalConfigs", configs = Array.Empty<object>() });
                PostToUi(new { action = "updateExternalStatus", statusText = $"Не удалось получить поддерживаемые конфигурации: источников с ошибками {loaded.FailedSources}/{sources.Count}." });
                return;
            }

            var live = new ConcurrentBag<ExternalScanCandidate>();
            var scanResult = await ExternalConfigScanner.ScanAsync(
                uniqueLinks,
                MeasurePingAsync,
                VerifyProxyTrafficAsync,
                checkedCount => PostToUi(new { action = "updateExternalStatus", statusText = $"Проверка конфигураций ({checkedCount}/{uniqueLinks.Count}); источников: {loaded.LoadedSources}/{sources.Count}..." }),
                trafficCount => PostToUi(new { action = "updateExternalStatus", statusText = $"Проверка HTTPS через VPN (проверено: {trafficCount}, найдено: {live.Count})..." }),
                configBatchSize: ExternalConfigBatchSize,
                trafficBatchSize: ExternalTrafficBatchSize,
                onVerified: candidate =>
                {
                    live.Add(candidate);
                    PostToUi(new
                    {
                        action = "updateExternalConfigs",
                        configs = live.OrderBy(c => c.Latency).Select(c => new
                        {
                            link = c.Link,
                            latency = c.Latency,
                            verificationStatus = new ServerProbeResult(c.Status).Label,
                            verifiedAt = c.VerifiedAt
                        }).ToList()
                    });
                },
                cancellationToken: scanToken);

            var sortedConfigs = scanResult.WorkingConfigs.Select(c => new
            {
                link = c.Link,
                latency = c.Latency,
                verificationStatus = new ServerProbeResult(c.Status).Label,
                verifiedAt = c.VerifiedAt
            }).ToList();

            var checkedAt = scanResult.WorkingConfigs.Select(c => c.VerifiedAt ?? DateTimeOffset.UtcNow).DefaultIfEmpty(DateTimeOffset.UtcNow).Min();
            try { File.WriteAllText(externalCachePath, JsonSerializer.Serialize(new { checkedAt, configs = sortedConfigs })); } catch { }

            PostToUi(new { action = "updateExternalConfigs", configs = sortedConfigs, checkedAt });

            int full = scanResult.WorkingConfigs.Count(c => c.Status == ProbeStatus.Verified);
            int partial = scanResult.WorkingConfigs.Count - full;
            int localErrors = scanResult.ErrorCounts.Where(kv => kv.Key is ProbeStatus.MissingEngine or ProbeStatus.EngineStartFailed or ProbeStatus.Busy).Sum(kv => kv.Value);
            string status = sortedConfigs.Count > 0 ? $"Трафик подтверждён: {full}, частичная доступность: {partial}"
                : localErrors > 0 ? $"Трафик не подтверждён; ошибок проверки: {localErrors}."
                : "Рабочий HTTPS-трафик не подтверждён";
            PostToUi(new { action = "updateExternalStatus", statusText = status + $" (проверено: {scanResult.TrafficChecks}/{uniqueLinks.Count}; источников: {loaded.LoadedSources}/{sources.Count})" });
        }
        catch (OperationCanceledException)
        {
            LogToUi("Проверка внешних списков остановлена пользователем.");
            PostToUi(new { action = "updateExternalStatus", statusText = "Проверка остановлена пользователем." });
        }
        catch (Exception ex)
        {
            LogToUi("Проверка внешних списков: " + ex.Message);
            PostToUi(new { action = "updateExternalStatus", statusText = "Ошибка проверки внешних списков. Подробности в логах." });
        }
        finally
        {
            externalScanInProgress = false;
            PostToUi(new { action = "updateExternalScanning", scanning = false });
        }
    }

    // --- SUBSCRIPTION & CLIPBOARD ---
    private async Task ImportSubscriptionAsync(string url)
    {
        if (string.IsNullOrEmpty(url)) return;
        LogToUi("Загрузка подписки по ссылке: " + url);

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            string content = await DesktopCoreServices.GetBoundedTextAsync(client, url);
            var parsedLinks = ExternalSources.ParseSubscription(content);
            int addedCount = 0;

            foreach (var link in parsedLinks)
            {
                if (!configHistory.Contains(link))
                {
                    configHistory.Add(link);
                    addedCount++;
                }
            }

            if (addedCount > 0)
            {
                SaveHistory();
                if (string.IsNullOrEmpty(currentConfigLink) && configHistory.Count > 0)
                {
                    currentConfigLink = configHistory.First();
                    SaveSettings();
                    PostToUi(new { action = "updateSelectedConfig", link = currentConfigLink });
                }
                PostToUi(new { action = "updateHistory", history = configHistory });
                PostToUi(new { action = "showToast", message = $"Импортировано серверов: {addedCount}" });
            }
            else
            {
                PostToUi(new { action = "showToast", message = "Новых рабочих ссылок не обнаружено" });
            }
        }
        catch (Exception ex)
        {
            LogToUi("Ошибка импорта подписки: " + ex.Message);
            PostToUi(new { action = "showToast", message = "Ошибка загрузки подписки" });
        }
    }

    private void PasteFromClipboard()
    {
        string? rawText = MacProxyManager.ReadClipboardText();
        if (string.IsNullOrWhiteSpace(rawText))
        {
            PostToUi(new { action = "showToast", message = "Буфер обмена пуст" });
            return;
        }

        string text = rawText.Trim();
        if (DesktopCoreServices.IsSupportedShareLink(text))
        {
            if (!configHistory.Contains(text))
            {
                configHistory.Insert(0, text);
                SaveHistory();
                currentConfigLink = text;
                SaveSettings();
                PostToUi(new { action = "updateSelectedConfig", link = currentConfigLink });
                PostToUi(new { action = "updateHistory", history = configHistory });
                PostToUi(new { action = "showToast", message = "Конфигурация добавлена!" });
            }
            else
            {
                currentConfigLink = text;
                SaveSettings();
                PostToUi(new { action = "updateSelectedConfig", link = currentConfigLink });
                PostToUi(new { action = "showToast", message = "Конфигурация выбрана!" });
            }
        }
        else
        {
            PostToUi(new { action = "showToast", message = "Неподдерживаемый формат ссылки в буфере обмена" });
        }
    }

    // --- BINARY AUTO-DOWNLOADER (SHA-256 VERIFIED) ---
    private async Task CheckAndDownloadBinariesAsync()
    {
        TrustedBinaryManifest manifest;
        try
        {
            manifest = TrustedBinaryManifest.Load(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "trusted-binaries.json"));
        }
        catch (Exception ex)
        {
            LogToUi("Загрузка модулей остановлена: доверенный manifest недоступен: " + ex.Message);
            PostToUi(new { action = "downloadProgress", downloading = false, progress = 0 });
            return;
        }

        bool isX64 = RuntimeInformation.OSArchitecture == Architecture.X64;
        string suffix = isX64 ? "-x64" : "";

        var xrayArtifact = manifest.GetRequired("xray" + suffix);
        var hysteriaArtifact = manifest.GetRequired("hysteria" + suffix);
        var mieruArtifact = manifest.GetRequired("mieru" + suffix);
        var tun2SocksArtifact = manifest.GetRequired("tun2socks" + suffix);
        var trustedInstallState = LoadTrustedInstallState();

        string xrayExe = Path.Combine(binariesDir, "xray");
        string hysteriaExe = Path.Combine(binariesDir, "hysteria");
        string mieruExe = Path.Combine(binariesDir, "mieru");
        string tun2socksExe = Path.Combine(binariesDir, "tun2socks");

        bool needsXray = !IsTrustedInstalled("xray", xrayExe, xrayArtifact, trustedInstallState)
            || !File.Exists(Path.Combine(binariesDir, "geoip.dat"))
            || !File.Exists(Path.Combine(binariesDir, "geosite.dat"));
        bool needsHysteria = !IsTrustedInstalled("hysteria", hysteriaExe, hysteriaArtifact, trustedInstallState);
        bool needsMieru = !IsTrustedInstalled("mieru", mieruExe, mieruArtifact, trustedInstallState);
        bool needsTun2Socks = !IsTrustedInstalled("tun2socks", tun2socksExe, tun2SocksArtifact, trustedInstallState);

        if (!needsXray && !needsHysteria && !needsMieru && !needsTun2Socks)
            return;

        PostToUi(new { action = "downloadProgress", downloading = true, progress = 0 });
        LogToUi("Запуск загрузки недостающих VPN-модулей для macOS...");

        try
        {
            int segments = (needsXray ? 1 : 0) + (needsHysteria ? 1 : 0) + (needsMieru ? 1 : 0) + (needsTun2Socks ? 1 : 0);
            int segSize = 100 / segments;
            int pos = 0;

            if (needsXray)
            {
                LogToUi("Загрузка Xray-core...");
                string xrayZip = Path.Combine(appDataDir, "xray.zip");
                await DownloadVerifiedArtifactWithProgressAsync(xrayArtifact, xrayZip, pos, pos + segSize);
                ZipFile.ExtractToDirectory(xrayZip, binariesDir, true);
                File.Delete(xrayZip);
                EnsureExecutablePermission(xrayExe);
                MarkTrustedInstalled("xray", xrayArtifact, trustedInstallState);
                SaveTrustedInstallState(trustedInstallState);
                LogToUi("Xray-core успешно установлен.");
                pos += segSize;
            }

            if (needsHysteria)
            {
                LogToUi("Загрузка Hysteria2...");
                await DownloadVerifiedArtifactWithProgressAsync(hysteriaArtifact, hysteriaExe, pos, pos + segSize);
                EnsureExecutablePermission(hysteriaExe);
                MarkTrustedInstalled("hysteria", hysteriaArtifact, trustedInstallState);
                SaveTrustedInstallState(trustedInstallState);
                LogToUi("Hysteria2 успешно установлен.");
                pos += segSize;
            }

            if (needsMieru)
            {
                LogToUi("Загрузка Mieru...");
                string mieruTarGz = Path.Combine(appDataDir, "mieru.tar.gz");
                await DownloadVerifiedArtifactWithProgressAsync(mieruArtifact, mieruTarGz, pos, pos + segSize);
                MacProxyManager.RunCommand("/usr/bin/tar", $"-xzf \"{mieruTarGz}\" -C \"{binariesDir}\"");
                File.Delete(mieruTarGz);
                EnsureExecutablePermission(mieruExe);
                MarkTrustedInstalled("mieru", mieruArtifact, trustedInstallState);
                SaveTrustedInstallState(trustedInstallState);
                LogToUi("Mieru успешно установлен.");
                pos += segSize;
            }

            if (needsTun2Socks)
            {
                LogToUi("Загрузка tun2socks...");
                string tunZip = Path.Combine(appDataDir, "tun2socks.zip");
                await DownloadVerifiedArtifactWithProgressAsync(tun2SocksArtifact, tunZip, pos, pos + segSize);
                ZipFile.ExtractToDirectory(tunZip, binariesDir, true);
                File.Delete(tunZip);
                string extractedName = isX64 ? "tun2socks-darwin-amd64" : "tun2socks-darwin-arm64";
                string extractedPath = Path.Combine(binariesDir, extractedName);
                if (File.Exists(extractedPath))
                {
                    File.Move(extractedPath, tun2socksExe, true);
                }
                EnsureExecutablePermission(tun2socksExe);
                MarkTrustedInstalled("tun2socks", tun2SocksArtifact, trustedInstallState);
                SaveTrustedInstallState(trustedInstallState);
                LogToUi("tun2socks успешно установлен.");
            }

            PostToUi(new { action = "downloadProgress", downloading = false, progress = 100 });
            LogToUi("Установка модулей для macOS полностью завершена!");
        }
        catch (Exception ex)
        {
            LogToUi("Ошибка скачивания модулей: " + ex.Message);
            PostToUi(new { action = "downloadProgress", downloading = false, progress = 0 });
        }
    }

    private static void EnsureExecutablePermission(string path)
    {
        if (!File.Exists(path)) return;
        MacProxyManager.RunCommand("/bin/chmod", $"+x \"{path}\"");
    }

    private async Task DownloadVerifiedArtifactWithProgressAsync(
        TrustedBinaryArtifact artifact,
        string destination,
        int startPercentage,
        int endPercentage)
    {
        string temporaryPath = destination + ".part";
        try
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            await DownloadFileWithProgressAsync(artifact.Url, temporaryPath, startPercentage, endPercentage);
            BinaryIntegrity.VerifySha256(temporaryPath, artifact.Sha256);
            File.Move(temporaryPath, destination, true);
            LogToUi($"Проверка SHA-256 пройдена: {Path.GetFileName(destination)}");
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }

    private async Task DownloadFileWithProgressAsync(string url, string destination, int startPercentage, int endPercentage)
    {
        using var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        using var client = new HttpClient(handler) { Timeout = BinaryDownloadTimeout };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ANARISE-VPN-MacClient/1.4");

        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        var totalBytes = response.Content.Headers.ContentLength ?? -1L;
        bool canReportProgress = totalBytes > 0;

        await using var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true);
        await using var stream = await response.Content.ReadAsStreamAsync();
        var buffer = new byte[8192];
        long totalRead = 0;
        int bytesRead;

        while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await fileStream.WriteAsync(buffer, 0, bytesRead);
            totalRead += bytesRead;
            if (canReportProgress)
            {
                double pct = (double)totalRead / totalBytes;
                int rel = (int)(startPercentage + pct * (endPercentage - startPercentage));
                PostToUi(new { action = "downloadProgress", downloading = true, progress = rel });
            }
        }
    }

    private Dictionary<string, string> LoadTrustedInstallState()
    {
        var path = Path.Combine(appDataDir, "trusted-installation.json");
        if (!File.Exists(path)) return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static bool IsTrustedInstalled(string name, string outputPath, TrustedBinaryArtifact artifact, IReadOnlyDictionary<string, string> state)
    {
        return File.Exists(outputPath)
            && state.TryGetValue(name, out var installedHash)
            && installedHash.Equals(artifact.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void MarkTrustedInstalled(string name, TrustedBinaryArtifact artifact, IDictionary<string, string> state)
    {
        state[name] = artifact.Sha256;
    }

    private void SaveTrustedInstallState(IReadOnlyDictionary<string, string> state)
    {
        var path = Path.Combine(appDataDir, "trusted-installation.json");
        var temp = path + ".part";
        File.WriteAllText(temp, JsonSerializer.Serialize(state));
        File.Move(temp, path, true);
    }

    // --- HELPERS & SETTINGS ---
    private void CheckForUpdates()
    {
        if (updateChecked) return;
        updateChecked = true;

        _ = Task.Run(async () =>
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.UserAgent.ParseAdd("AnariseVPN-MacClient");
                using var response = await client.GetAsync("https://api.github.com/repos/pasnya/anarise-vpn/releases?per_page=10");
                if (response.IsSuccessStatusCode)
                {
                    string jsonString = await response.Content.ReadAsStringAsync();
                    var updateResult = UpdateVersionHelper.FindNewerRelease(jsonString, AppVersion, "macos");
                    if (updateResult != null)
                    {
                        PostToUi(new { action = "updateAvailable", version = updateResult.LatestVersion, url = updateResult.DownloadUrl });
                    }
                }
            }
            catch { }
        });
    }

    private bool EnsureLocalPortsAvailable()
    {
        try
        {
            int originalSocks = socksPort;
            int originalHttp = httpPort;

            if (!DesktopCoreServices.IsLocalPortAvailable(socksPort, requireUdp: true))
            {
                socksPort = DesktopCoreServices.FindAvailableLocalPort(20808, requireUdp: true, excludedPort: httpPort);
                LogToUi($"SOCKS-порт {originalSocks} занят. Автоматически выбран порт {socksPort}.");
            }

            if (httpPort == socksPort || !DesktopCoreServices.IsLocalPortAvailable(httpPort, requireUdp: false))
            {
                httpPort = DesktopCoreServices.FindAvailableLocalPort(20809, requireUdp: false, excludedPort: socksPort);
                LogToUi($"HTTP-порт {originalHttp} занят. Автоматически выбран порт {httpPort}.");
            }

            if (socksPort != originalSocks || httpPort != originalHttp)
            {
                SaveSettings();
                SendSettingsAndHistory();
            }
            return true;
        }
        catch (Exception ex)
        {
            LogToUi("Ошибка проверки локальных портов: " + ex.Message);
            return false;
        }
    }

    private void KillOrphanedProcesses()
    {
        try
        {
            string[] names = { "xray", "tun2socks", "hysteria", "mieru" };
            foreach (var name in names)
            {
                foreach (var proc in Process.GetProcessesByName(name))
                {
                    try
                    {
                        string? modPath = null;
                        try { modPath = proc.MainModule?.FileName; } catch { }
                        if (modPath != null && modPath.StartsWith(binariesDir, StringComparison.OrdinalIgnoreCase))
                        {
                            proc.Kill(true);
                            proc.WaitForExit(2000);
                        }
                    }
                    catch { }
                    finally { proc.Dispose(); }
                }
            }
        }
        catch { }
    }

    private void CleanupRuntimeConfigFiles()
    {
        foreach (var fileName in new[] { "config.json", "xray_config.json", "hysteria_config.json", "mieru_config.json", "geo_router_config.json" })
        {
            try
            {
                var path = Path.Combine(appDataDir, fileName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch { }
        }
    }

    private void UpdateSetting(string name, JsonNode val)
    {
        switch (name)
        {
            case "killSwitch":
                killSwitch = val.GetValue<bool>();
                if (!killSwitch)
                {
                    MacProxyManager.SetKillSwitchBlocked(false);
                }
                break;
            case "autostart":
                autostart = val.GetValue<bool>();
                MacProxyManager.SetAutostartLaunchAgent(autostart);
                break;
            case "autoreconnect":
                autoreconnect = val.GetValue<bool>();
                break;
            case "bypassLan":
                bypassLan = val.GetValue<bool>();
                if (isConnected && !vpnMode && systemProxy)
                {
                    MacProxyManager.SetProxy(true, socksPort, httpPort, bypassLan,
                        enableHttpProxy: activeEnableHttpProxy,
                        username: proxyAuthEnabled ? proxyUsername : null, password: proxyAuthEnabled ? proxyPassword : null);
                }
                break;
            case "systemProxy":
                systemProxy = val.GetValue<bool>();
                if (isConnected && !vpnMode)
                {
                    MacProxyManager.SetProxy(systemProxy, socksPort, httpPort, bypassLan,
                        enableHttpProxy: activeEnableHttpProxy,
                        username: proxyAuthEnabled ? proxyUsername : null, password: proxyAuthEnabled ? proxyPassword : null);
                }
                break;
            case "proxyAuthEnabled":
                proxyAuthEnabled = val.GetValue<bool>();
                if (isConnected) LogToUi("Аутентификация применяется при следующем подключении.");
                break;
            case "geoRoutingRules":
                try
                {
                    string rules = val.GetValue<string>() ?? "";
                    GeoRouting.Parse(rules);
                    geoRoutingRules = rules;
                    if (isConnected) LogToUi("Geo-правила сохранены и применятся при следующем подключении.");
                }
                catch (FormatException ex)
                {
                    LogToUi("Geo-правила не сохранены: " + ex.Message);
                    PostToUi(new { action = "showToast", message = "Проверьте формат geo-правил" });
                    PostToUi(new { action = "updateSettings", settings = new { geoRoutingRules } });
                }
                break;
            case "vpnMode":
                vpnMode = val.GetValue<bool>();
                break;
            case "socksPort":
                int sp = val.GetValue<int>();
                if (sp >= 1 && sp <= 65535) socksPort = sp;
                break;
            case "httpPort":
                int hp = val.GetValue<int>();
                if (hp >= 1 && hp <= 65535) httpPort = hp;
                break;
        }
        SaveSettings();
    }

    private void LoadSettings()
    {
        if (!SecureStorage.TryRead(settingsPath, out var json))
        {
            json = TryMigratePlaintext(settingsPath);
        }

        if (json != null)
        {
            try
            {
                var doc = JsonNode.Parse(json);
                if (doc != null)
                {
                    killSwitch = doc["killSwitch"]?.GetValue<bool>() ?? false;
                    autostart = doc["autostart"]?.GetValue<bool>() ?? false;
                    autoreconnect = doc["autoreconnect"]?.GetValue<bool>() ?? true;
                    bypassLan = doc["bypassLan"]?.GetValue<bool>() ?? true;
                    systemProxy = doc["systemProxy"]?.GetValue<bool>() ?? true;
                    vpnMode = doc["vpnMode"]?.GetValue<bool>() ?? false;
                    socksPort = doc["socksPort"]?.GetValue<int>() ?? 20808;
                    httpPort = doc["httpPort"]?.GetValue<int>() ?? 20809;
                    proxyAuthEnabled = doc["proxyAuthEnabled"]?.GetValue<bool>() ?? true;
                    proxyUsername = doc["proxyUsername"]?.ToString() ?? proxyUsername;
                    proxyPassword = doc["proxyPassword"]?.ToString() ?? proxyPassword;
                    geoRoutingRules = doc["geoRoutingRules"]?.ToString() ?? "";
                    currentConfigLink = doc["currentConfigLink"]?.ToString() ?? "";
                }
            }
            catch { }
        }
    }

    private void SaveSettings()
    {
        try
        {
            var node = new JsonObject
            {
                ["killSwitch"] = killSwitch,
                ["autostart"] = autostart,
                ["autoreconnect"] = autoreconnect,
                ["bypassLan"] = bypassLan,
                ["systemProxy"] = systemProxy,
                ["vpnMode"] = vpnMode,
                ["socksPort"] = socksPort,
                ["httpPort"] = httpPort,
                ["proxyAuthEnabled"] = proxyAuthEnabled,
                ["proxyUsername"] = proxyUsername,
                ["proxyPassword"] = proxyPassword,
                ["geoRoutingRules"] = geoRoutingRules,
                ["currentConfigLink"] = currentConfigLink
            };
            SecureStorage.Write(settingsPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void LoadHistory()
    {
        if (!SecureStorage.TryRead(historyPath, out var json))
        {
            json = TryMigratePlaintext(historyPath);
        }

        if (json != null)
        {
            try
            {
                var list = JsonSerializer.Deserialize<List<string>>(json);
                if (list != null)
                {
                    configHistory.Clear();
                    configHistory.AddRange(list);
                }
            }
            catch { }
        }
    }

    private void SaveHistory()
    {
        try
        {
            SecureStorage.Write(historyPath, JsonSerializer.Serialize(configHistory, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private static string? TryMigratePlaintext(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            var plaintext = File.ReadAllText(path);
            SecureStorage.Write(path, plaintext);
            return plaintext;
        }
        catch
        {
            return null;
        }
    }

    public void OnClosing()
    {
        Disconnect(preserveKillSwitch: false);
    }
}
