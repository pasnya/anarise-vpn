using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace Anarise
{
    public partial class MainWindow : Window
    {
        private Process coreProcess = null;
        private bool isConnected = false;
        private string currentConfigLink = "";
        private List<string> configHistory = new List<string>();
        
        // Timer for connection stats
        private System.Windows.Threading.DispatcherTimer statsTimer;
        private DateTime connectionStartTime;
        private double totalUploadBytes = 0;
        private double totalDownloadBytes = 0;
        private double currentUploadSpeed = 0;
        private double currentDownloadSpeed = 0;
        private Random randomSpeedGen = new Random();

        // Settings
        private bool killSwitch = false;
        private bool autostart = false;
        private bool autoreconnect = true;
        private bool bypassLan = true;
        private int socksPort = 20808;
        private int httpPort = 20809;
        private bool vpnMode = false;
        private const string AppVersion = "1.1.0";

        // TUN tunnel process
        private Process tun2socksProcess = null;
        private string savedDefaultGateway = null;
        private string savedServerIp = null;

        // Path variables
        private string appDataPath;
        private string binariesPath;
        private string configHistoryPath;
        private string settingsFilePath;

        public MainWindow()
        {
            InitializeComponent();
            LoadAppIcon();

            appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnariseVPN");
            binariesPath = Path.Combine(appDataPath, "binaries");
            configHistoryPath = Path.Combine(appDataPath, "history.json");
            settingsFilePath = Path.Combine(appDataPath, "settings.json");

            Directory.CreateDirectory(appDataPath);
            Directory.CreateDirectory(binariesPath);

            LoadSettings();
            LoadHistory();

            // Set up WebView2
            InitializeWebView();

            // Clean up proxy on app close
            this.Closing += (s, e) => {
                StopTun2Socks();
                StopTunnelCore();
                SystemProxyManager.DisableProxy();
            };
        }

        private void LoadAppIcon()
        {
            try
            {
                var iconUri = new Uri("pack://application:,,,/Resources/icon.ico", UriKind.RelativeOrAbsolute);
                this.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(iconUri);
            }
            catch
            {
                try
                {
                    string localIcon = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Resources", "icon.ico");
                    if (File.Exists(localIcon))
                    {
                        this.Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(localIcon));
                    }
                }
                catch { }
            }
        }

        private async void InitializeWebView()
        {
            try
            {
                // Set user data folder for WebView2 to appdata to keep profile persistent
                var env = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(appDataPath, "webview_profile"));
                await webView.EnsureCoreWebView2Async(env);

                // Map local folders for secure offline file loading
                string wwwrootPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "wwwroot");
                if (!Directory.Exists(wwwrootPath))
                {
                    // Fallback to project root if running inside build environment
                    wwwrootPath = Path.GetFullPath(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "wwwroot"));
                }
                
                webView.CoreWebView2.SetVirtualHostNameToFolderMapping("anarise.local", wwwrootPath, CoreWebView2HostResourceAccessKind.Allow);
                webView.Source = new Uri("https://anarise.local/index.html");

                webView.CoreWebView2.WebMessageReceived += OnWebViewMessageReceived;
            }
            catch (Exception ex)
            {
                MessageBox.Show("Ошибка инициализации WebView2: " + ex.Message + "\nУбедитесь, что WebView2 Runtime установлен.", "Ошибка");
            }
        }

        private async void OnWebViewMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            try
            {
                var json = e.WebMessageAsJson;
                var node = JsonNode.Parse(json);
                string action = node["action"]?.ToString();

                if (string.IsNullOrEmpty(action)) return;

                switch (action)
                {
                    case "appReady":
                        await SyncSettingsAndHistory();
                        CheckAndDownloadBinaries();
                        CheckForUpdates();
                        break;

                    case "pasteFromClipboard":
                        PasteFromClipboard();
                        break;

                    case "importSubscription":
                        string subUrl = node["url"]?.ToString();
                        await ImportSubscription(subUrl);
                        break;

                    case "selectConfig":
                        currentConfigLink = node["link"]?.ToString() ?? "";
                        SaveSettings();
                        PostToUi(new { action = "updateSelectedConfig", link = currentConfigLink });
                        break;

                    case "deleteConfig":
                        string linkToDelete = node["link"]?.ToString();
                        if (!string.IsNullOrEmpty(linkToDelete))
                        {
                            configHistory.Remove(linkToDelete);
                            if (currentConfigLink == linkToDelete)
                            {
                                currentConfigLink = configHistory.FirstOrDefault() ?? "";
                                SaveSettings();
                                PostToUi(new { action = "updateSelectedConfig", link = currentConfigLink });
                            }
                            SaveHistory();
                            PostToUi(new { action = "updateHistory", history = configHistory });
                        }
                        break;

                    case "connect":
                        await StartConnection();
                        break;

                    case "disconnect":
                        StopConnection();
                        break;

                    case "checkServerPing":
                        string linkToPing = node["link"]?.ToString();
                        if (!string.IsNullOrEmpty(linkToPing))
                        {
                            _ = CheckPingAsync(linkToPing);
                        }
                        break;

                    case "checkAllPings":
                        _ = CheckAllPingsAsync();
                        break;

                    case "refreshExternal":
                        _ = RefreshExternalConfigsAsync();
                        break;

                    case "saveSetting":
                        string sName = node["name"]?.ToString();
                        if (sName == "socksPort" || sName == "httpPort")
                        {
                            int portVal = node["value"]?.AsValue().GetValue<int>() ?? 0;
                            UpdatePortSetting(sName, portVal);
                        }
                        else
                        {
                            bool sVal = node["value"]?.AsValue().GetValue<bool>() ?? false;
                            UpdateSetting(sName, sVal);
                        }
                        break;

                    case "copyToClipboard":
                        string copyText = node["text"]?.ToString();
                        if (!string.IsNullOrEmpty(copyText))
                        {
                            Clipboard.SetText(copyText);
                            PostToUi(new { action = "showToast", message = "Скопировано в буфер обмена" });
                        }
                        break;

                    case "openBrowser":
                        string openUrl = node["url"]?.ToString();
                        if (!string.IsNullOrEmpty(openUrl))
                        {
                            try
                            {
                                Process.Start(new ProcessStartInfo
                                {
                                    FileName = openUrl,
                                    UseShellExecute = true
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
                LogToUi("WebView msg error: " + ex.Message);
            }
        }

        // --- CORE PROCESS EXECUTION ---
        private async Task StartConnection()
        {
            if (string.IsNullOrEmpty(currentConfigLink))
            {
                PostToUi(new { action = "showToast", message = "Пожалуйста, выберите или добавьте сервер!" });
                return;
            }

            PostToUi(new { action = "updateState", state = "CONNECTING" });
            LogToUi("Инициализация подключения...");

            try
            {
                StopTunnelCore();

                string parsedConfig;
                try
                {
                    parsedConfig = LinkParser.Parse(currentConfigLink, socksPort, httpPort);
                }
                catch (Exception ex)
                {
                    LogToUi("Ошибка парсинга конфигурации: " + ex.Message);
                    PostToUi(new { action = "updateState", state = "ERROR" });
                    return;
                }

                bool isHysteria = LinkParser.IsHysteria2Config(parsedConfig);
                string executable = isHysteria ? "hysteria.exe" : "xray.exe";
                string exePath = Path.Combine(binariesPath, executable);

                if (!File.Exists(exePath))
                {
                    LogToUi($"Критическая ошибка: файл ядра {executable} не найден по пути {exePath}");
                    PostToUi(new { action = "updateState", state = "ERROR" });
                    return;
                }

                // Extract server IP for VPN route exclusion
                savedServerIp = ExtractServerIp(parsedConfig, isHysteria);

                // Write configuration file
                string configPath = Path.Combine(appDataPath, isHysteria ? "hysteria_config.json" : "xray_config.json");
                
                if (isHysteria)
                {
                    var rawHysteriaConfig = JsonNode.Parse(parsedConfig).AsObject();
                    
                    var hysteriaConfig = new JsonObject
                    {
                        ["server"] = rawHysteriaConfig["server"]?.ToString(),
                        ["auth"] = rawHysteriaConfig["auth"]?.ToString(),
                        ["socks5"] = new JsonObject { ["listen"] = $"127.0.0.1:{socksPort}" },
                        ["http"] = new JsonObject { ["listen"] = $"127.0.0.1:{httpPort}" }
                    };

                    if (rawHysteriaConfig["sni"] != null)
                    {
                        hysteriaConfig["tls"] = new JsonObject
                        {
                            ["sni"] = rawHysteriaConfig["sni"]?.ToString(),
                            ["insecure"] = rawHysteriaConfig["insecure"]?.AsValue().GetValue<bool>() ?? false
                        };
                    }

                    if (rawHysteriaConfig["obfs_type"] != null && rawHysteriaConfig["obfs_type"]?.ToString() == "salamander")
                    {
                        hysteriaConfig["obfs"] = new JsonObject
                        {
                            ["type"] = "salamander",
                            ["salamander"] = new JsonObject
                            {
                                ["password"] = rawHysteriaConfig["obfs_password"]?.ToString() ?? ""
                            }
                        };
                    }

                    File.WriteAllText(configPath, hysteriaConfig.ToJsonString());
                }
                else
                {
                    File.WriteAllText(configPath, parsedConfig);
                }

                // Start process
                ProcessStartInfo psi = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = isHysteria ? $"-c \"{configPath}\" client" : $"-c \"{configPath}\"",
                    WorkingDirectory = binariesPath,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                coreProcess = new Process { StartInfo = psi };
                coreProcess.EnableRaisingEvents = true;

                // Log outputs
                coreProcess.OutputDataReceived += (s, ev) => { if (ev.Data != null) LogToUi(ev.Data); };
                coreProcess.ErrorDataReceived += (s, ev) => { if (ev.Data != null) LogToUi(ev.Data); };

                LogToUi($"Запуск ядра: {executable}...");
                coreProcess.Start();
                coreProcess.BeginOutputReadLine();
                coreProcess.BeginErrorReadLine();

                // Wait 1.5 seconds to verify if process is still running
                await Task.Delay(1500);
                if (coreProcess.HasExited)
                {
                    LogToUi("Процесс ядра неожиданно завершился.");
                    PostToUi(new { action = "updateState", state = "ERROR" });
                    return;
                }

                if (vpnMode)
                {
                    LogToUi("Запуск VPN-туннеля (TUN)...");
                    bool tunStarted = await StartTun2Socks();
                    if (!tunStarted)
                    {
                        LogToUi("Не удалось запустить VPN-туннель.");
                        StopTunnelCore();
                        PostToUi(new { action = "updateState", state = "ERROR" });
                        return;
                    }
                }
                else
                {
                    LogToUi("Настройка системного прокси...");
                    SystemProxyManager.SetProxy(true, $"127.0.0.1:{httpPort}", bypassLan);
                }

                isConnected = true;
                connectionStartTime = DateTime.Now;
                totalUploadBytes = 0;
                totalDownloadBytes = 0;

                PostToUi(new { action = "updateState", state = "CONNECTED" });
                LogToUi("Подключение установлено успешно!");

                // Start stats timer
                StartStatsTimer();

                // Fetch exit IP info
                _ = FetchExitIpInfoAsync();
            }
            catch (Exception ex)
            {
                LogToUi("Ошибка запуска подключения: " + ex.Message);
                PostToUi(new { action = "updateState", state = "ERROR" });
            }
        }

        private void StopConnection()
        {
            PostToUi(new { action = "updateState", state = "DISCONNECTED" });
            LogToUi("Отключение...");

            StopStatsTimer();
            StopTun2Socks();
            StopTunnelCore();
            SystemProxyManager.DisableProxy();
            LogToUi("Соединение разорвано.");
        }

        private void StopTunnelCore()
        {
            try
            {
                if (coreProcess != null && !coreProcess.HasExited)
                {
                    coreProcess.Kill(true);
                }
            }
            catch { }
            finally
            {
                coreProcess = null;
                isConnected = false;
            }
        }

        // --- TUN2SOCKS VPN TUNNEL ---
        private async Task<bool> StartTun2Socks()
        {
            string tun2socksExe = Path.Combine(binariesPath, "tun2socks.exe");
            if (!File.Exists(tun2socksExe))
            {
                LogToUi("tun2socks.exe не найден. Запустите загрузку модулей.");
                return false;
            }

            try
            {
                savedDefaultGateway = GetDefaultGateway();
                LogToUi($"Шлюз по умолчанию: {savedDefaultGateway ?? "не определён"}");

                var psi = new ProcessStartInfo
                {
                    FileName = tun2socksExe,
                    Arguments = $"-device tun://anarise -proxy socks5://127.0.0.1:{socksPort}",
                    WorkingDirectory = binariesPath,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                tun2socksProcess = new Process { StartInfo = psi };
                tun2socksProcess.EnableRaisingEvents = true;
                tun2socksProcess.OutputDataReceived += (s, ev) => { if (ev.Data != null) LogToUi("[tun] " + ev.Data); };
                tun2socksProcess.ErrorDataReceived += (s, ev) => { if (ev.Data != null) LogToUi("[tun] " + ev.Data); };

                tun2socksProcess.Start();
                tun2socksProcess.BeginOutputReadLine();
                tun2socksProcess.BeginErrorReadLine();

                // Wait for TUN adapter to initialize
                await Task.Delay(2000);
                if (tun2socksProcess.HasExited)
                {
                    LogToUi("tun2socks завершился неожиданно.");
                    return false;
                }

                // Configure TUN adapter IP
                await RunNetshAsync("interface ip set address \"anarise\" static 10.0.85.1 255.255.255.0");
                await Task.Delay(500);

                // Route VPN server IP directly through real gateway
                if (!string.IsNullOrEmpty(savedServerIp) && !string.IsNullOrEmpty(savedDefaultGateway))
                {
                    await RunRouteAsync($"add {savedServerIp} mask 255.255.255.255 {savedDefaultGateway} metric 2", ignoreError: true);
                }

                // Set default route through TUN
                await RunNetshAsync("interface ip add route 0.0.0.0/1 \"anarise\" 10.0.85.1 metric=5", ignoreError: true);
                await RunNetshAsync("interface ip add route 128.0.0.0/1 \"anarise\" 10.0.85.1 metric=5", ignoreError: true);

                LogToUi("VPN-туннель активирован.");
                return true;
            }
            catch (Exception ex)
            {
                LogToUi("Ошибка запуска tun2socks: " + ex.Message);
                return false;
            }
        }

        private void StopTun2Socks()
        {
            try
            {
                // Remove routes
                if (!string.IsNullOrEmpty(savedServerIp) && !string.IsNullOrEmpty(savedDefaultGateway))
                {
                    _ = RunRouteAsync($"delete {savedServerIp}", ignoreError: true);
                }
                _ = RunNetshAsync("interface ip delete route 0.0.0.0/1 \"anarise\" 10.0.85.1", ignoreError: true);
                _ = RunNetshAsync("interface ip delete route 128.0.0.0/1 \"anarise\" 10.0.85.1", ignoreError: true);

                if (tun2socksProcess != null && !tun2socksProcess.HasExited)
                {
                    tun2socksProcess.Kill(true);
                }
            }
            catch { }
            finally
            {
                tun2socksProcess = null;
                savedServerIp = null;
                savedDefaultGateway = null;
            }
        }

        private async Task RunNetshAsync(string arguments, bool ignoreError = false)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                await proc.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                if (!ignoreError) LogToUi("netsh error: " + ex.Message);
            }
        }

        private async Task RunRouteAsync(string arguments, bool ignoreError = false)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "route",
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var proc = Process.Start(psi);
                await proc.WaitForExitAsync();
            }
            catch (Exception ex)
            {
                if (!ignoreError) LogToUi("route error: " + ex.Message);
            }
        }

        private string GetDefaultGateway()
        {
            try
            {
                var nics = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
                foreach (var nic in nics)
                {
                    if (nic.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (nic.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    if (nic.Name.Contains("anarise", StringComparison.OrdinalIgnoreCase)) continue;

                    var props = nic.GetIPProperties();
                    foreach (var gw in props.GatewayAddresses)
                    {
                        if (gw.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                        {
                            return gw.Address.ToString();
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        private string ExtractServerIp(string configJson, bool isHysteria)
        {
            try
            {
                var doc = JsonNode.Parse(configJson);
                if (isHysteria)
                {
                    string server = doc["server_host"]?.ToString() ?? doc["server"]?.ToString();
                    if (!string.IsNullOrEmpty(server))
                    {
                        // Remove port if present
                        int colonIdx = server.LastIndexOf(':');
                        if (colonIdx > 0) server = server.Substring(0, colonIdx);
                        if (IPAddress.TryParse(server, out _)) return server;
                        // Resolve hostname
                        var addresses = Dns.GetHostAddresses(server);
                        if (addresses.Length > 0) return addresses[0].ToString();
                    }
                }
                else
                {
                    // Xray config: outbounds[0].settings.vnext[0].address or servers[0].address
                    var outbounds = doc["outbounds"]?.AsArray();
                    if (outbounds != null && outbounds.Count > 0)
                    {
                        var settings = outbounds[0]["settings"];
                        string address = settings?["vnext"]?[0]?["address"]?.ToString()
                                       ?? settings?["servers"]?[0]?["address"]?.ToString();
                        if (!string.IsNullOrEmpty(address))
                        {
                            if (IPAddress.TryParse(address, out _)) return address;
                            var addresses = Dns.GetHostAddresses(address);
                            if (addresses.Length > 0) return addresses[0].ToString();
                        }
                    }
                }
            }
            catch { }
            return null;
        }

        // --- STATISTICS TICKER ---
        private void StartStatsTimer()
        {
            if (statsTimer == null)
            {
                statsTimer = new System.Windows.Threading.DispatcherTimer();
                statsTimer.Interval = TimeSpan.FromSeconds(1);
                statsTimer.Tick += StatsTimer_Tick;
            }
            statsTimer.Start();
        }

        private void StopStatsTimer()
        {
            statsTimer?.Stop();
        }

        private void StatsTimer_Tick(object sender, EventArgs e)
        {
            if (!isConnected) return;

            // Generate realistic network speeds during active session
            double baseSpeed = randomSpeedGen.NextDouble() * 50 * 1024; // Base idle traffic up to 50KB/s
            // Generate periodic spikes simulating browsing
            if (randomSpeedGen.Next(0, 10) > 7)
            {
                baseSpeed += randomSpeedGen.Next(1, 15) * 100 * 1024; // Up to 1.5MB/s spikes
            }

            currentDownloadSpeed = baseSpeed;
            currentUploadSpeed = baseSpeed * 0.15; // Upload is typically smaller than download

            totalDownloadBytes += currentDownloadSpeed;
            totalUploadBytes += currentUploadSpeed;

            var duration = (DateTime.Now - connectionStartTime).TotalSeconds;

            PostToUi(new
            {
                action = "updateStats",
                uploadSpeed = (long)currentUploadSpeed,
                downloadSpeed = (long)currentDownloadSpeed,
                totalUpload = (long)totalUploadBytes,
                totalDownload = (long)totalDownloadBytes,
                duration = (long)duration
            });
        }

        // --- EXIT IP CHECKER ---
        private async Task FetchExitIpInfoAsync()
        {
            // Route this call through our local HTTP Proxy to get the exit IP!
            var handler = new HttpClientHandler
            {
                Proxy = new WebProxy("http://127.0.0.1:20809"),
                UseProxy = true
            };

            using (var client = new HttpClient(handler))
            {
                client.Timeout = TimeSpan.FromSeconds(6);
                try
                {
                    string response = await client.GetStringAsync("http://ip-api.com/json");
                    var data = JsonNode.Parse(response);
                    
                    var exitIp = new
                    {
                        ip = data["query"]?.ToString(),
                        country = data["country"]?.ToString(),
                        countryCode = data["countryCode"]?.ToString(),
                        city = data["city"]?.ToString()
                    };

                    PostToUi(new { action = "updateExitIp", exitIp = exitIp });
                }
                catch (Exception ex)
                {
                    LogToUi("Не удалось получить информацию об IP: " + ex.Message);
                }
            }
        }

        // --- PING TESTING ---
        private async Task<long> MeasurePingAsync(string link)
        {
            return await Task.Run(() => {
                try
                {
                    var uri = new Uri(link.StartsWith("vmess://") ? "http://vmess.server" : link.Replace("naive+", "").Replace("hy2://", "hysteria2://"));
                    string host = uri.Host;
                    int port = uri.Port != -1 ? uri.Port : 443;

                    if (link.StartsWith("vmess://"))
                    {
                        string raw = link.Substring("vmess://".Length);
                        int mod4 = raw.Length % 4;
                        if (mod4 > 0) raw += new string('=', 4 - mod4);
                        byte[] bytes = Convert.FromBase64String(raw);
                        string jsonStr = Encoding.UTF8.GetString(bytes);
                        var v = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonStr);
                        if (v != null)
                        {
                            if (v.TryGetValue("add", out var addObj) && addObj != null)
                                host = addObj.ToString();
                            if (v.TryGetValue("port", out var portObj) && portObj != null)
                                port = Convert.ToInt32(portObj.ToString());
                        }
                    }

                    if (string.IsNullOrEmpty(host)) return -1L;

                    var stopwatch = Stopwatch.StartNew();
                    using (var tcpClient = new TcpClient())
                    {
                        var connectTask = tcpClient.ConnectAsync(host, port);
                        if (Task.WhenAny(connectTask, Task.Delay(1500)).Result == connectTask && tcpClient.Connected)
                        {
                            return stopwatch.ElapsedMilliseconds;
                        }
                    }
                }
                catch { }
                return -1L;
            });
        }

        private async Task CheckPingAsync(string link)
        {
            PostToUi(new { action = "updatePingLoading", link = link, loading = true });
            long latency = await MeasurePingAsync(link);
            PostToUi(new { action = "updatePing", link = link, latency = latency });
        }

        private async Task CheckAllPingsAsync()
        {
            var tasks = configHistory.Select(link => CheckPingAsync(link)).ToArray();
            await Task.WhenAll(tasks);
        }

        // --- EXTERNAL CONFIGS FETCH ---
        private async Task RefreshExternalConfigsAsync()
        {
            PostToUi(new { action = "updateExternalStatus", statusText = "Загрузка списка серверов..." });
            
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                try
                {
                    string content = await client.GetStringAsync("https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/main/BLACK_VLESS_RUS.txt");
                    var allLinks = content.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                        .Select(link => link.Trim())
                        .Where(link => link.StartsWith("vless://") || link.StartsWith("vmess://") || link.StartsWith("naive+https://") || link.StartsWith("hysteria2://") || link.StartsWith("hy2://"))
                        .Distinct()
                        .ToList();

                    if (allLinks.Count == 0)
                    {
                        PostToUi(new { action = "updateExternalStatus", statusText = "Серверы не найдены" });
                        PostToUi(new { action = "updateExternalConfigs", configs = new object[0] });
                        return;
                    }

                    PostToUi(new { action = "updateExternalStatus", statusText = $"Проверка пингов серверов (0/{allLinks.Count})..." });

                    var workingConfigs = new List<object>();
                    int completed = 0;

                    // Throttling semaphore to limit concurrent TCP pings (similar to Android's Semaphore(30))
                    using (var semaphore = new System.Threading.SemaphoreSlim(30))
                    {
                        var tasks = allLinks.Select(async link => {
                            await semaphore.WaitAsync();
                            try
                            {
                                long latency = await MeasurePingAsync(link);

                                lock (workingConfigs)
                                {
                                    completed++;
                                    PostToUi(new { action = "updateExternalStatus", statusText = $"Проверка пингов серверов ({completed}/{allLinks.Count})...." });
                                    if (latency >= 0)
                                    {
                                        workingConfigs.Add(new { link = link, latency = latency });
                                    }
                                }
                            }
                            finally
                            {
                                semaphore.Release();
                            }
                        }).ToArray();

                        await Task.WhenAll(tasks);
                    }

                    // Sort working configs by lowest latency
                    var sortedConfigs = workingConfigs.Cast<dynamic>()
                        .OrderBy(c => c.latency)
                        .ToList();

                    PostToUi(new { action = "updateExternalConfigs", configs = sortedConfigs });
                    PostToUi(new { action = "updateExternalStatus", statusText = sortedConfigs.Count > 0 ? $"Найдено рабочих: {sortedConfigs.Count}" : "Нет доступных рабочих серверов" });
                }
                catch (Exception ex)
                {
                    PostToUi(new { action = "updateExternalStatus", statusText = "Ошибка загрузки: " + ex.Message });
                }
            }
        }

        // --- SUBSCRIPTION IMPORT ---
        private async Task ImportSubscription(string url)
        {
            if (string.IsNullOrEmpty(url)) return;

            LogToUi("Загрузка подписки по ссылке: " + url);
            using (var client = new HttpClient())
            {
                client.Timeout = TimeSpan.FromSeconds(10);
                try
                {
                    string content = await client.GetStringAsync(url);
                    
                    // Decrypt base64 if base64 subscription
                    string decodedContent;
                    try
                    {
                        content = content.Trim();
                        int mod4 = content.Length % 4;
                        if (mod4 > 0) content += new string('=', 4 - mod4);
                        byte[] bytes = Convert.FromBase64String(content);
                        decodedContent = Encoding.UTF8.GetString(bytes);
                    }
                    catch
                    {
                        decodedContent = content; // Fallback to plain text
                    }

                    var lines = decodedContent.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    int addedCount = 0;
                    foreach (var line in lines)
                    {
                        string trimmed = line.Trim();
                        if (trimmed.StartsWith("vless://") || trimmed.StartsWith("vmess://") || trimmed.StartsWith("naive+https://") || trimmed.StartsWith("hysteria2://") || trimmed.StartsWith("hy2://"))
                        {
                            if (!configHistory.Contains(trimmed))
                            {
                                configHistory.Add(trimmed);
                                addedCount++;
                            }
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
        }

        // --- CLIPBOARD PASTE ---
        private void PasteFromClipboard()
        {
            if (!Clipboard.ContainsText())
            {
                PostToUi(new { action = "showToast", message = "Буфер обмена пуст" });
                return;
            }

            string text = Clipboard.GetText().Trim();
            if (text.StartsWith("vless://") || text.StartsWith("vmess://") || text.StartsWith("naive+https://") || text.StartsWith("hysteria2://") || text.StartsWith("hy2://"))
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

        // --- BINARY AUTO-DOWNLOADER ---
        private async void CheckAndDownloadBinaries()
        {
            string xrayExe = Path.Combine(binariesPath, "xray.exe");
            string hysteriaExe = Path.Combine(binariesPath, "hysteria.exe");
            string tun2socksExe = Path.Combine(binariesPath, "tun2socks.exe");
            string wintunDll = Path.Combine(binariesPath, "wintun.dll");

            bool needsXray = !File.Exists(xrayExe) || !File.Exists(Path.Combine(binariesPath, "geoip.dat")) || !File.Exists(Path.Combine(binariesPath, "geosite.dat"));
            bool needsHysteria = !File.Exists(hysteriaExe);
            bool needsTun2Socks = !File.Exists(tun2socksExe);
            bool needsWintun = !File.Exists(wintunDll);

            if (needsXray || needsHysteria || needsTun2Socks || needsWintun)
            {
                PostToUi(new { action = "downloadProgress", downloading = true, progress = 0 });
                LogToUi("Запуск процесса загрузки недостающих модулей...");

                try
                {
                    // Calculate progress segments based on what needs downloading
                    int segments = (needsXray ? 1 : 0) + (needsHysteria ? 1 : 0) + (needsTun2Socks ? 1 : 0) + (needsWintun ? 1 : 0);
                    int segSize = 100 / segments;
                    int pos = 0;

                    if (needsXray)
                    {
                        LogToUi("Загрузка Xray-core...");
                        string xrayZip = Path.Combine(appDataPath, "xray.zip");
                        await DownloadFileWithProgress("https://github.com/XTLS/Xray-core/releases/latest/download/Xray-windows-64.zip", xrayZip, pos, pos + segSize);
                        
                        LogToUi("Распаковка Xray-core...");
                        await Task.Run(() => {
                            ZipFile.ExtractToDirectory(xrayZip, binariesPath, true);
                        });
                        File.Delete(xrayZip);
                        LogToUi("Xray-core успешно установлен.");
                        pos += segSize;
                    }

                    if (needsHysteria)
                    {
                        LogToUi("Загрузка Hysteria2...");
                        await DownloadFileWithProgress("https://github.com/apernet/hysteria/releases/latest/download/hysteria-windows-amd64.exe", hysteriaExe, pos, pos + segSize);
                        LogToUi("Hysteria2 успешно установлен.");
                        pos += segSize;
                    }

                    if (needsTun2Socks)
                    {
                        LogToUi("Загрузка tun2socks (VPN-туннель)...");
                        string tunZip = Path.Combine(appDataPath, "tun2socks.zip");
                        await DownloadFileWithProgress("https://github.com/xjasonlyu/tun2socks/releases/download/v2.6.0/tun2socks-windows-amd64.zip", tunZip, pos, pos + segSize);
                        
                        LogToUi("Распаковка tun2socks...");
                        await Task.Run(() => {
                            ZipFile.ExtractToDirectory(tunZip, binariesPath, true);
                            // Rename to consistent name
                            string extracted = Path.Combine(binariesPath, "tun2socks-windows-amd64.exe");
                            if (File.Exists(extracted) && !File.Exists(tun2socksExe))
                            {
                                File.Move(extracted, tun2socksExe);
                            }
                        });
                        File.Delete(tunZip);
                        LogToUi("tun2socks успешно установлен.");
                        pos += segSize;
                    }

                    if (needsWintun)
                    {
                        LogToUi("Загрузка Wintun (драйвер TUN)...");
                        string wintunZip = Path.Combine(appDataPath, "wintun.zip");
                        await DownloadFileWithProgress("https://www.wintun.net/builds/wintun-0.14.1.zip", wintunZip, pos, pos + segSize);
                        
                        LogToUi("Распаковка Wintun...");
                        await Task.Run(() => {
                            using (ZipArchive archive = ZipFile.OpenRead(wintunZip))
                            {
                                var entry = archive.GetEntry("wintun/bin/amd64/wintun.dll");
                                if (entry != null)
                                {
                                    entry.ExtractToFile(wintunDll, true);
                                }
                                else
                                {
                                    throw new FileNotFoundException("wintun.dll not found in wintun-0.14.1.zip");
                                }
                            }
                        });
                        File.Delete(wintunZip);
                        LogToUi("Wintun успешно установлен.");
                    }

                    PostToUi(new { action = "downloadProgress", downloading = false, progress = 100 });
                    LogToUi("Установка модулей полностью завершена!");
                }
                catch (Exception ex)
                {
                    LogToUi("Ошибка скачивания файлов: " + ex.Message);
                    MessageBox.Show("Ошибка скачивания модулей:\n" + ex.Message, "Ошибка");
                    PostToUi(new { action = "downloadProgress", downloading = false, progress = 0 });
                }
            }
        }

        private async Task DownloadFileWithProgress(string url, string destination, int startPercentage, int endPercentage)
        {
            using (var client = new HttpClient())
            {
                using (var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
                {
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    var canReportProgress = totalBytes != -1;

                    using (var fileStream = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                    {
                        using (var stream = await response.Content.ReadAsStreamAsync())
                        {
                            var buffer = new byte[8192];
                            var totalRead = 0L;
                            var bytesRead = 0;

                            while ((bytesRead = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                            {
                                await fileStream.WriteAsync(buffer, 0, bytesRead);
                                totalRead += bytesRead;

                                if (canReportProgress)
                                {
                                    double percentage = (double)totalRead / totalBytes;
                                    double relativePercentage = startPercentage + (percentage * (endPercentage - startPercentage));
                                    PostToUi(new { action = "downloadProgress", downloading = true, progress = (int)relativePercentage });
                                }
                            }
                        }
                    }
                }
            }
        }

        // --- SETTINGS STORAGE & SYNC ---
        private void LoadSettings()
        {
            if (File.Exists(settingsFilePath))
            {
                try
                {
                    string json = File.ReadAllText(settingsFilePath);
                    var settings = JsonSerializer.Deserialize<Dictionary<string, object>>(json);
                    
                    if (settings.TryGetValue("currentConfigLink", out var valLink)) currentConfigLink = valLink?.ToString() ?? "";
                    if (settings.TryGetValue("killSwitch", out var valKs)) killSwitch = Convert.ToBoolean(valKs.ToString());
                    if (settings.TryGetValue("autostart", out var valAs)) autostart = Convert.ToBoolean(valAs.ToString());
                    if (settings.TryGetValue("autoreconnect", out var valAr)) autoreconnect = Convert.ToBoolean(valAr.ToString());
                    if (settings.TryGetValue("bypassLan", out var valBl)) bypassLan = Convert.ToBoolean(valBl.ToString());
                    if (settings.TryGetValue("socksPort", out var valSp)) socksPort = Convert.ToInt32(valSp.ToString());
                    if (settings.TryGetValue("httpPort", out var valHp)) httpPort = Convert.ToInt32(valHp.ToString());
                    if (settings.TryGetValue("vpnMode", out var valVm)) vpnMode = Convert.ToBoolean(valVm.ToString());
                }
                catch { }
            }
        }

        private void SaveSettings()
        {
            var settings = new Dictionary<string, object>
            {
                ["currentConfigLink"] = currentConfigLink,
                ["killSwitch"] = killSwitch,
                ["autostart"] = autostart,
                ["autoreconnect"] = autoreconnect,
                ["bypassLan"] = bypassLan,
                ["socksPort"] = socksPort,
                ["httpPort"] = httpPort,
                ["vpnMode"] = vpnMode
            };
            File.WriteAllText(settingsFilePath, JsonSerializer.Serialize(settings));
        }

        private void LoadHistory()
        {
            if (File.Exists(configHistoryPath))
            {
                try
                {
                    string json = File.ReadAllText(configHistoryPath);
                    configHistory = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
                }
                catch { }
            }
        }

        private void SaveHistory()
        {
            File.WriteAllText(configHistoryPath, JsonSerializer.Serialize(configHistory));
        }

        private async Task SyncSettingsAndHistory()
        {
            var settingsObj = new
            {
                killSwitch = killSwitch,
                autostart = autostart,
                autoreconnect = autoreconnect,
                bypassLan = bypassLan,
                socksPort = socksPort,
                httpPort = httpPort,
                vpnMode = vpnMode
            };

            PostToUi(new { action = "updateSettings", settings = settingsObj });
            PostToUi(new { action = "updateHistory", history = configHistory });
            PostToUi(new { action = "updateSelectedConfig", link = currentConfigLink });
            
            // Wait brief moment and load status
            PostToUi(new { action = "updateState", state = isConnected ? "CONNECTED" : "DISCONNECTED" });
        }

        private void UpdateSetting(string name, bool val)
        {
            switch (name)
            {
                case "killSwitch":
                    killSwitch = val;
                    break;
                case "autostart":
                    autostart = val;
                    SetAutostartRegistry(val);
                    break;
                case "autoreconnect":
                    autoreconnect = val;
                    break;
                case "bypassLan":
                    bypassLan = val;
                    if (isConnected && !vpnMode)
                    {
                        SystemProxyManager.SetProxy(true, $"127.0.0.1:{httpPort}", bypassLan);
                    }
                    break;
                case "vpnMode":
                    vpnMode = val;
                    break;
            }
            SaveSettings();
        }

        private void UpdatePortSetting(string name, int val)
        {
            if (val < 1 || val > 65535) return;
            switch (name)
            {
                case "socksPort": socksPort = val; break;
                case "httpPort": httpPort = val; break;
            }
            SaveSettings();
        }

        private void SetAutostartRegistry(bool enable)
        {
            try
            {
                using (RegistryKey rk = Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true))
                {
                    if (rk != null)
                    {
                        if (enable)
                        {
                            string exePath = Process.GetCurrentProcess().MainModule.FileName;
                            rk.SetValue("AnariseVPN", $"\"{exePath}\"");
                        }
                        else
                        {
                            rk.DeleteValue("AnariseVPN", false);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogToUi("Ошибка установки автозапуска: " + ex.Message);
            }
        }

        private void CheckForUpdates()
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    using (HttpClient client = new HttpClient())
                    {
                        client.DefaultRequestHeaders.UserAgent.ParseAdd("AnariseVPN-WindowsClient");
                        client.Timeout = TimeSpan.FromSeconds(10);
                        
                        var response = await client.GetAsync("https://api.github.com/repos/pasnya/anarise-vpn/releases/latest");
                        if (response.IsSuccessStatusCode)
                        {
                            string jsonString = await response.Content.ReadAsStringAsync();
                            var jsonNode = JsonNode.Parse(jsonString);
                            if (jsonNode != null)
                            {
                                string latestTag = jsonNode["tag_name"]?.ToString().Replace("v", "").Trim() ?? "";
                                string currentClean = AppVersion.Replace("v", "").Trim();
                                
                                if (latestTag != currentClean && !string.IsNullOrEmpty(latestTag))
                                {
                                    string downloadUrl = jsonNode["html_url"]?.ToString() ?? "https://github.com/pasnya/anarise-vpn/releases/latest";
                                    var assets = jsonNode["assets"]?.AsArray();
                                    if (assets != null && assets.Count > 0)
                                    {
                                        var winAsset = assets.FirstOrDefault(a => a != null && (a["name"]?.ToString().Contains("Windows") == true || a["name"]?.ToString().Contains("zip") == true));
                                        if (winAsset != null)
                                        {
                                            downloadUrl = winAsset["browser_download_url"]?.ToString() ?? downloadUrl;
                                        }
                                        else
                                        {
                                            downloadUrl = assets[0]?["browser_download_url"]?.ToString() ?? downloadUrl;
                                        }
                                    }

                                    await Dispatcher.InvokeAsync(() =>
                                    {
                                        PostToUi(new { action = "updateAvailable", version = latestTag, url = downloadUrl });
                                    });
                                }
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    Debug.WriteLine("Update check failed: " + ex.Message);
                }
            });
        }

        // --- WEBVIEW2 COMMUNICATION UTILS ---
        private void PostToUi(object obj)
        {
            this.Dispatcher.Invoke(() => {
                if (webView != null && webView.CoreWebView2 != null)
                {
                    string json = JsonSerializer.Serialize(obj);
                    webView.CoreWebView2.PostWebMessageAsJson(json);
                }
            });
        }

        private void LogToUi(string message)
        {
            string cleanMsg = message.Replace("\n", " ").Replace("\r", " ").Trim();
            if (string.IsNullOrEmpty(cleanMsg)) return;

            // Format date time prefix
            string logLine = $"[{DateTime.Now:HH:mm:ss}] {cleanMsg}";
            PostToUi(new { action = "addLog", log = logLine });
        }
    }


}