using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
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
        MacNetworkStateGuard.RecoverPreviousSessionIfNeeded();

        string appDir = AppDomain.CurrentDomain.BaseDirectory;
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
    private readonly CoreProcessManager coreManager = new();
    private readonly StatisticsService statsService = new();
    private readonly string appDataDir;
    private readonly string settingsPath;
    private readonly string historyPath;
    private readonly string binariesDir;

    private readonly List<string> configHistory = new();
    private string currentConfigLink = "";
    private bool killSwitch = false;
    private bool autostart = false;
    private bool autoreconnect = false;
    private bool bypassLan = true;
    private bool systemProxy = true;
    private bool vpnMode = false;
    private int socksPort = 20808;
    private int httpPort = 20809;

    private string currentState = "DISCONNECTED";
    private DateTime connectionStartTime = DateTime.UtcNow;
    private CancellationTokenSource? externalScanCts;
    private System.Threading.Timer? statsTimer;

    public MacAppController()
    {
        appDataDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".anarise");
        Directory.CreateDirectory(appDataDir);
        settingsPath = Path.Combine(appDataDir, "settings.json");
        historyPath = Path.Combine(appDataDir, "history.json");
        binariesDir = Path.Combine(appDataDir, "bin");
        Directory.CreateDirectory(binariesDir);

        LoadSettings();
        LoadHistory();
    }

    public void AttachWindow(PhotinoWindow win)
    {
        window = win;
    }

    public void HandleWebMessage(string message)
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
                    _ = CheckAndDownloadBinariesAsync();
                    break;

                case "connect":
                    _ = ConnectAsync();
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

                case "pasteFromClipboard":
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
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MacAppController] HandleWebMessage error: {ex.Message}");
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
                vpnMode,
                systemProxy
            }
        });
        PostToUi(new { action = "updateHistory", history = configHistory });
        PostToUi(new { action = "updateSelectedConfig", link = currentConfigLink });
        PostToUi(new { action = "updateState", state = currentState });
    }

    private async Task ConnectAsync()
    {
        if (string.IsNullOrWhiteSpace(currentConfigLink))
        {
            PostToUi(new { action = "showToast", message = "Выберите сервер для подключения" });
            return;
        }

        currentState = "CONNECTING";
        PostToUi(new { action = "updateState", state = currentState });
        PostToUi(new { action = "addLog", text = "Подготовка к подключению..." });

        try
        {
            string parsedConfig = LinkParser.Parse(currentConfigLink, socksPort, httpPort);
            bool isHysteria = LinkParser.IsHysteria2Config(parsedConfig);
            bool isMieru = LinkParser.IsMieruConfig(parsedConfig);

            string configFilePath = Path.Combine(appDataDir, "config.json");
            File.WriteAllText(configFilePath, parsedConfig);

            string coreBinaryName = isHysteria ? "hysteria" : isMieru ? "mieru" : "xray";
            string coreBinary = Path.Combine(binariesDir, coreBinaryName);

            var startInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = coreBinary,
                Arguments = isMieru ? $"run -c \"{configFilePath}\"" : isHysteria ? $"-c \"{configFilePath}\"" : $"-c \"{configFilePath}\"",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            coreManager.Start(startInfo,
                outLine => PostToUi(new { action = "addLog", text = outLine }),
                errLine => PostToUi(new { action = "addLog", text = $"[err] {errLine}" })
            );

            await Task.Delay(1000);

            if (systemProxy)
            {
                MacProxyManager.SetProxy(true, socksPort, httpPort, bypassLan);
                MacNetworkStateGuard.RecordSession(true, killSwitch, MacProxyManager.GetActiveService());
            }

            currentState = "CONNECTED";
            connectionStartTime = DateTime.UtcNow;
            PostToUi(new { action = "updateState", state = currentState });
            PostToUi(new { action = "showToast", message = "Подключено!" });

            statsService.Reset();
            statsTimer?.Dispose();
            statsTimer = new System.Threading.Timer(_ => UpdateStatsTick(), null, 1000, 1000);

            _ = FetchExitIpInfoAsync();
        }
        catch (Exception ex)
        {
            currentState = "ERROR";
            PostToUi(new { action = "updateState", state = currentState });
            PostToUi(new { action = "addLog", text = $"Ошибка подключения: {ex.Message}" });
            Disconnect(preserveKillSwitch: false);
        }
    }

    private void Disconnect(bool preserveKillSwitch)
    {
        coreManager.Stop();
        statsTimer?.Dispose();
        statsTimer = null;

        if (systemProxy)
        {
            MacProxyManager.SetProxy(false);
        }

        if (!preserveKillSwitch)
        {
            MacProxyManager.SetKillSwitchBlocked(false);
            MacNetworkStateGuard.ClearSession();
        }

        currentState = "DISCONNECTED";
        PostToUi(new { action = "updateState", state = currentState });
        PostToUi(new { action = "updateStats", uploadSpeed = 0L, downloadSpeed = 0L, totalUpload = 0L, totalDownload = 0L, duration = 0L });
    }

    private void UpdateStatsTick()
    {
        try
        {
            var duration = (long)(DateTime.UtcNow - connectionStartTime).TotalSeconds;
            var sample = statsService.Update(0, 0); // Polling deltas
            PostToUi(new
            {
                action = "updateStats",
                uploadSpeed = sample.UploadSpeed,
                downloadSpeed = sample.DownloadSpeed,
                totalUpload = sample.TotalUpload,
                totalDownload = sample.TotalDownload,
                duration
            });
        }
        catch { }
    }

    private async Task FetchExitIpInfoAsync()
    {
        try
        {
            var handler = new HttpClientHandler
            {
                Proxy = new System.Net.WebProxy($"socks5://127.0.0.1:{socksPort}"),
                UseProxy = true
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(8) };
            var json = await client.GetStringAsync("https://ipwho.is/");
            var doc = JsonNode.Parse(json);
            if (doc != null && doc["ip"] != null)
            {
                string ip = doc["ip"]?.ToString() ?? "";
                string country = doc["country"]?.ToString() ?? "";
                string city = doc["city"]?.ToString() ?? "";
                string flag = doc["country_code"]?.ToString() ?? "";
                PostToUi(new { action = "updateExitIp", ip, country, city, flag });
            }
        }
        catch { }
    }

    private async Task CheckPingAsync(string link)
    {
        PostToUi(new { action = "updatePingLoading", link, loading = true });
        try
        {
            var probe = new ServerProbe(binariesDir, Path.Combine(appDataDir, "temp"));
            var result = await probe.CheckAsync(link);
            PostToUi(new { action = "updatePing", link, ping = result.Latency, status = result.Status.ToString() });
        }
        catch
        {
            PostToUi(new { action = "updatePing", link, ping = -1, status = "Error" });
        }
        finally
        {
            PostToUi(new { action = "updatePingLoading", link, loading = false });
        }
    }

    private async Task CheckAllPingsAsync()
    {
        foreach (var link in configHistory)
        {
            await CheckPingAsync(link);
        }
    }

    private async Task ScanExternalSourcesAsync()
    {
        externalScanCts?.Cancel();
        externalScanCts = new CancellationTokenSource();
        var token = externalScanCts.Token;

        PostToUi(new { action = "updateExternalScanning", scanning = true });
        PostToUi(new { action = "updateExternalStatus", text = "Загрузка внешних списков..." });

        try
        {
            string catalogPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "external-sources.json");
            var sources = ExternalSources.Load(catalogPath);
            var loadResult = await ExternalSourceLoader.FetchAsync(sources, log => PostToUi(new { action = "updateExternalStatus", text = log }));

            PostToUi(new { action = "updateExternalStatus", text = $"Проверка {loadResult.Links.Count} конфигураций..." });

            var probe = new ServerProbe(binariesDir, Path.Combine(appDataDir, "temp"));
            var scanResult = await ExternalConfigScanner.ScanAsync(
                loadResult.Links,
                measurePing: async link =>
                {
                    token.ThrowIfCancellationRequested();
                    var pr = await probe.CheckAsync(link);
                    return pr.IsUsable ? pr.Latency : -1L;
                },
                verifyTraffic: async link =>
                {
                    token.ThrowIfCancellationRequested();
                    return await probe.CheckAsync(link);
                },
                onPingProgress: null,
                onTrafficProgress: null,
                configBatchSize: 200,
                trafficBatchSize: 30,
                onVerified: null,
                cancellationToken: token
            );

            PostToUi(new
            {
                action = "updateExternalConfigs",
                configs = scanResult.WorkingConfigs.Select(c => new { link = c.Link, latency = c.Latency, status = c.Status.ToString() })
            });

            PostToUi(new { action = "updateExternalStatus", text = $"Найдено рабочих серверов: {scanResult.WorkingConfigs.Count}" });
        }
        catch (OperationCanceledException)
        {
            PostToUi(new { action = "updateExternalStatus", text = "Сканирование остановлено пользователем." });
        }
        catch (Exception ex)
        {
            PostToUi(new { action = "updateExternalStatus", text = $"Ошибка сканирования: {ex.Message}" });
        }
        finally
        {
            PostToUi(new { action = "updateExternalScanning", scanning = false });
        }
    }

    private async Task CheckAndDownloadBinariesAsync()
    {
        await Task.CompletedTask;
    }

    private void UpdateSetting(string name, JsonNode val)
    {
        switch (name)
        {
            case "killSwitch": killSwitch = val.GetValue<bool>(); break;
            case "autostart": autostart = val.GetValue<bool>(); break;
            case "autoreconnect": autoreconnect = val.GetValue<bool>(); break;
            case "bypassLan": bypassLan = val.GetValue<bool>(); break;
            case "systemProxy": systemProxy = val.GetValue<bool>(); break;
            case "vpnMode": vpnMode = val.GetValue<bool>(); break;
            case "socksPort": socksPort = val.GetValue<int>(); break;
            case "httpPort": httpPort = val.GetValue<int>(); break;
        }
        SaveSettings();
    }

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(settingsPath))
            {
                var doc = JsonNode.Parse(File.ReadAllText(settingsPath));
                if (doc != null)
                {
                    killSwitch = doc["killSwitch"]?.GetValue<bool>() ?? false;
                    autostart = doc["autostart"]?.GetValue<bool>() ?? false;
                    autoreconnect = doc["autoreconnect"]?.GetValue<bool>() ?? false;
                    bypassLan = doc["bypassLan"]?.GetValue<bool>() ?? true;
                    systemProxy = doc["systemProxy"]?.GetValue<bool>() ?? true;
                    vpnMode = doc["vpnMode"]?.GetValue<bool>() ?? false;
                    socksPort = doc["socksPort"]?.GetValue<int>() ?? 20808;
                    httpPort = doc["httpPort"]?.GetValue<int>() ?? 20809;
                    currentConfigLink = doc["currentConfigLink"]?.ToString() ?? "";
                }
            }
        }
        catch { }
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
                ["currentConfigLink"] = currentConfigLink
            };
            File.WriteAllText(settingsPath, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    private void LoadHistory()
    {
        try
        {
            if (File.Exists(historyPath))
            {
                var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(historyPath));
                if (list != null)
                {
                    configHistory.Clear();
                    configHistory.AddRange(list);
                }
            }
        }
        catch { }
    }

    private void SaveHistory()
    {
        try
        {
            File.WriteAllText(historyPath, JsonSerializer.Serialize(configHistory, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    public void OnClosing()
    {
        Disconnect(preserveKillSwitch: false);
    }
}
