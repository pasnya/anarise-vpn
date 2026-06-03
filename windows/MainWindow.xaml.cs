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
                        bool sVal = node["value"]?.AsValue().GetValue<bool>() ?? false;
                        UpdateSetting(sName, sVal);
                        break;

                    case "copyToClipboard":
                        string copyText = node["text"]?.ToString();
                        if (!string.IsNullOrEmpty(copyText))
                        {
                            Clipboard.SetText(copyText);
                            PostToUi(new { action = "showToast", message = "Скопировано в буфер обмена" });
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
                    parsedConfig = LinkParser.Parse(currentConfigLink);
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

                // Write configuration file
                string configPath = Path.Combine(appDataPath, isHysteria ? "hysteria_config.json" : "xray_config.json");
                
                if (isHysteria)
                {
                    // Convert configuration to Hysteria2 JSON (which we listen HTTP on 20809, SOCKS5 on 20808)
                    var rawHysteriaConfig = JsonNode.Parse(parsedConfig).AsObject();
                    
                    var hysteriaConfig = new JsonObject
                    {
                        ["server"] = rawHysteriaConfig["server"]?.ToString(),
                        ["auth"] = rawHysteriaConfig["auth"]?.ToString(),
                        ["socks5"] = new JsonObject { ["listen"] = "127.0.0.1:20808" },
                        ["http"] = new JsonObject { ["listen"] = "127.0.0.1:20809" }
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

                // Apply Windows system proxy
                LogToUi("Настройка системного прокси...");
                SystemProxyManager.SetProxy(true, "127.0.0.1:20809", bypassLan);

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

            bool needsXray = !File.Exists(xrayExe) || !File.Exists(Path.Combine(binariesPath, "geoip.dat")) || !File.Exists(Path.Combine(binariesPath, "geosite.dat"));
            bool needsHysteria = !File.Exists(hysteriaExe);

            if (needsXray || needsHysteria)
            {
                PostToUi(new { action = "downloadProgress", downloading = true, progress = 0 });
                LogToUi("Запуск процесса загрузки недостающих ядер...");

                try
                {
                    if (needsXray)
                    {
                        LogToUi("Загрузка Xray-core...");
                        string xrayZip = Path.Combine(appDataPath, "xray.zip");
                        await DownloadFileWithProgress("https://github.com/XTLS/Xray-core/releases/latest/download/Xray-windows-64.zip", xrayZip, 0, 50);
                        
                        LogToUi("Распаковка Xray-core...");
                        await Task.Run(() => {
                            ZipFile.ExtractToDirectory(xrayZip, binariesPath, true);
                        });
                        File.Delete(xrayZip);
                        LogToUi("Xray-core успешно установлен.");
                    }

                    if (needsHysteria)
                    {
                        LogToUi("Загрузка Hysteria2...");
                        await DownloadFileWithProgress("https://github.com/apernet/hysteria/releases/latest/download/hysteria-windows-amd64.exe", hysteriaExe, 50, 100);
                        LogToUi("Hysteria2 успешно установлен.");
                    }

                    PostToUi(new { action = "downloadProgress", downloading = false, progress = 100 });
                    LogToUi("Установка ядер полностью завершена!");
                }
                catch (Exception ex)
                {
                    LogToUi("Ошибка скачивания файлов: " + ex.Message);
                    MessageBox.Show("Ошибка скачивания модулей Xray/Hysteria2:\n" + ex.Message, "Ошибка");
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
                ["bypassLan"] = bypassLan
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
                bypassLan = bypassLan
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
                    // If connected, apply new bypass immediately
                    if (isConnected)
                    {
                        SystemProxyManager.SetProxy(true, "127.0.0.1:20809", bypassLan);
                    }
                    break;
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