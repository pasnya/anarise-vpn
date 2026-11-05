using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace Anarise;

public static class DesktopCoreServices
{
    public const int MaxSubscriptionBytes = 1 * 1024 * 1024;

    private static readonly HttpClient DnsOverHttpsClient = new(new HttpClientHandler
    {
        // DNS bootstrap must never use the Windows/macOS system proxy or resolver.
        UseProxy = false
    })
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public static async Task<string> ResolveDohAsync(string hostname, Action<string>? log = null)
    {
        if (string.IsNullOrEmpty(hostname)) return hostname;

        if (IPAddress.TryParse(hostname, out var literalAddress))
        {
            if (literalAddress.AddressFamily != AddressFamily.InterNetwork)
            {
                throw new InvalidOperationException($"IPv6-адрес {hostname} не поддерживается. Используйте IPv4-конфиг.");
            }
            return hostname;
        }

        log?.Invoke($"Разрешение адреса {hostname} через DoH...");

        // 1. Try Cloudflare DoH (1.1.1.1)
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://1.1.1.1/dns-query?name={Uri.EscapeDataString(hostname)}&type=A");
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/dns-json"));

            using var response = await DnsOverHttpsClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("Answer", out var answerProp) && answerProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in answerProp.EnumerateArray())
                    {
                        if (element.TryGetProperty("type", out var typeProp) && typeProp.GetInt32() == 1 &&
                            element.TryGetProperty("data", out var dataProp))
                        {
                            string ip = dataProp.GetString() ?? "";
                            if (IPAddress.TryParse(ip, out var parsedIp) && parsedIp.AddressFamily == AddressFamily.InterNetwork)
                            {
                                log?.Invoke($"Адрес {hostname} разрешён: {ip}");
                                return ip;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"DoH ошибка разрешения для {hostname}: {ex.Message}. Переход к резервному серверу.");
        }

        // 2. Fallback to Google DoH (8.8.8.8)
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://8.8.8.8/resolve?name={Uri.EscapeDataString(hostname)}&type=A");
            request.Headers.Accept.Clear();
            request.Headers.Accept.Add(new System.Net.Http.Headers.MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await DnsOverHttpsClient.SendAsync(request);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync();
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("Answer", out var answerProp) && answerProp.ValueKind == JsonValueKind.Array)
                {
                    foreach (var element in answerProp.EnumerateArray())
                    {
                        if (element.TryGetProperty("type", out var typeProp) && typeProp.GetInt32() == 1 &&
                            element.TryGetProperty("data", out var dataProp))
                        {
                            string ip = dataProp.GetString() ?? "";
                            if (IPAddress.TryParse(ip, out var parsedIp) && parsedIp.AddressFamily == AddressFamily.InterNetwork)
                            {
                                log?.Invoke($"Адрес {hostname} разрешён через Google DoH: {ip}");
                                return ip;
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            log?.Invoke($"Google DoH ошибка разрешения для {hostname}: {ex.Message}");
        }

        throw new InvalidOperationException($"Не удалось разрешить {hostname} через защищённый DoH. Локальный DNS не используется.");
    }

    public static async Task<object?> FetchExitIpInfoAsync(int socksPort, Action<string>? log = null, string? proxyUsername = null, string? proxyPassword = null)
    {
        var proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}");
        if (!string.IsNullOrEmpty(proxyUsername) && !string.IsNullOrEmpty(proxyPassword))
            proxy.Credentials = new NetworkCredential(proxyUsername, proxyPassword);
        var handler = new HttpClientHandler
        {
            Proxy = proxy,
            UseProxy = true
        };

        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
        try
        {
            string response = await client.GetStringAsync("https://ipwho.is/");
            var data = JsonNode.Parse(response);
            if (data != null && data["success"]?.GetValue<bool>() != false)
            {
                return new
                {
                    ip = data["ip"]?.ToString() ?? data["query"]?.ToString(),
                    country = data["country"]?.ToString(),
                    countryCode = data["country_code"]?.ToString() ?? data["countryCode"]?.ToString(),
                    city = data["city"]?.ToString()
                };
            }
        }
        catch
        {
            // Fallback to ip-api
        }

        try
        {
            string response = await client.GetStringAsync("http://ip-api.com/json");
            var data = JsonNode.Parse(response);
            return new
            {
                ip = data?["query"]?.ToString(),
                country = data?["country"]?.ToString(),
                countryCode = data?["countryCode"]?.ToString(),
                city = data?["city"]?.ToString()
            };
        }
        catch (Exception ex)
        {
            log?.Invoke("Не удалось получить информацию об IP: " + ex.Message);
            return null;
        }
    }

    public static async Task<long> MeasureTcpPingAsync(string link)
    {
        try
        {
            var config = JsonNode.Parse(LinkParser.Parse(link))!.AsObject();
            if (LinkParser.IsHysteria2Config(config.ToJsonString())) return 0; // QUIC/UDP, no TCP gate.
            string host;
            int port;
            if (LinkParser.IsMieruConfig(config.ToJsonString()))
            {
                if (!string.Equals(config["transport"]?.ToString(), "TCP", StringComparison.OrdinalIgnoreCase)) return 0;
                host = config["server_host"]!.ToString();
                port = config["server_port"]!.GetValue<int>();
            }
            else
            {
                var settings = config["outbounds"]![0]!["settings"]!;
                var server = (settings["vnext"] ?? settings["servers"])![0]!;
                host = server["address"]!.ToString();
                port = server["port"]!.GetValue<int>();
            }
            var timer = Stopwatch.StartNew();
            using var socket = new TcpClient();
            using var timeout = new CancellationTokenSource(2500);
            await socket.ConnectAsync(host, port, timeout.Token);
            return timer.ElapsedMilliseconds;
        }
        catch
        {
            return -1;
        }
    }

    public static bool IsLocalPortAvailable(int port, bool requireUdp)
    {
        if (port < 1 || port > 65535) return false;

        try
        {
            using var tcpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            if (OperatingSystem.IsWindows())
            {
                tcpSocket.ExclusiveAddressUse = true;
            }
            tcpSocket.Bind(new IPEndPoint(IPAddress.Loopback, port));

            if (requireUdp)
            {
                using var udpSocket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                if (OperatingSystem.IsWindows())
                {
                    udpSocket.ExclusiveAddressUse = true;
                }
                udpSocket.Bind(new IPEndPoint(IPAddress.Loopback, port));
            }

            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    public static int FindAvailableLocalPort(int preferredPort, bool requireUdp, int excludedPort)
    {
        for (int offset = 0; offset < 200; offset++)
        {
            int candidate = preferredPort + offset;
            if (candidate > 65535 || candidate == excludedPort) continue;
            if (IsLocalPortAvailable(candidate, requireUdp)) return candidate;
        }

        throw new InvalidOperationException("No free local proxy port was found.");
    }

    public static string? ExtractServerIp(string configJson)
    {
        try
        {
            var doc = JsonNode.Parse(configJson);
            var protocol = doc?["_protocol"]?.ToString();
            if (protocol == LinkParser.PROTOCOL_HYSTERIA2 || protocol == LinkParser.PROTOCOL_MIERU)
            {
                string? server = doc?["server_host"]?.ToString() ?? doc?["server"]?.ToString();
                if (!string.IsNullOrEmpty(server))
                {
                    int colonIdx = server.LastIndexOf(':');
                    if (colonIdx > 0) server = server.Substring(0, colonIdx);
                    if (IPAddress.TryParse(server, out _)) return server;
                    return null;
                }
            }
            else
            {
                var outbounds = doc?["outbounds"]?.AsArray();
                if (outbounds != null && outbounds.Count > 0)
                {
                    var settings = outbounds[0]?["settings"];
                    string? address = settings?["vnext"]?[0]?["address"]?.ToString()
                                   ?? settings?["servers"]?[0]?["address"]?.ToString();
                    if (!string.IsNullOrEmpty(address))
                    {
                        if (IPAddress.TryParse(address, out _)) return address;
                        return null;
                    }
                }
            }
        }
        catch { }
        return null;
    }

    public static string BuildRuntimeCoreConfig(string parsedConfig, int socksPort, int httpPort, bool isMieru, bool isHysteria, string? proxyUsername = null, string? proxyPassword = null)
    {
        if (isMieru)
        {
            var rawMieruConfig = JsonNode.Parse(parsedConfig)!.AsObject();
            string host = rawMieruConfig["server_host"]?.ToString() ?? "";
            bool isIp = IPAddress.TryParse(host, out _);
            string ipAddressVal = isIp ? host : "";
            string domainNameVal = isIp ? "" : host;
            string protoVal = (rawMieruConfig["transport"]?.ToString() ?? "TCP").ToUpperInvariant();
            string multiplexingVal = (rawMieruConfig["multiplexing"]?.ToString() ?? "MULTIPLEXING_LOW").ToUpperInvariant();

            var profile = new JsonObject
            {
                ["profileName"] = "default",
                ["user"] = new JsonObject
                {
                    ["name"] = rawMieruConfig["username"]?.ToString(),
                    ["password"] = rawMieruConfig["password"]?.ToString()
                },
                ["servers"] = new JsonArray(new JsonObject
                {
                    ["ipAddress"] = ipAddressVal,
                    ["domainName"] = domainNameVal,
                    ["portBindings"] = new JsonArray(new JsonObject
                    {
                        ["port"] = rawMieruConfig["server_port"]?.AsValue().GetValue<int>() ?? 443,
                        ["protocol"] = protoVal
                    })
                }),
                ["mtu"] = 1350,
                ["multiplexing"] = new JsonObject
                {
                    ["level"] = multiplexingVal
                },
                ["handshakeMode"] = "HANDSHAKE_STANDARD"
            };

            var mieruConfig = new JsonObject
            {
                ["profiles"] = new JsonArray(profile),
                ["activeProfile"] = "default",
                ["rpcPort"] = 20810,
                ["socks5Port"] = socksPort,
                ["socks5ListenLAN"] = false,
                ["loggingLevel"] = "INFO"
            };

            if (!string.IsNullOrWhiteSpace(proxyUsername) && !string.IsNullOrEmpty(proxyPassword))
            {
                mieruConfig["socks5Authentication"] = new JsonArray(new JsonObject
                {
                    ["user"] = proxyUsername,
                    ["password"] = proxyPassword
                });
            }

            return mieruConfig.ToJsonString();
        }

        if (isHysteria)
        {
            var rawHysteriaConfig = JsonNode.Parse(parsedConfig)!.AsObject();
            var hysteriaConfig = new JsonObject
            {
                ["server"] = rawHysteriaConfig["server"]?.ToString(),
                ["auth"] = rawHysteriaConfig["auth"]?.ToString(),
                ["socks5"] = new JsonObject { ["listen"] = $"127.0.0.1:{socksPort}" },
                ["http"] = new JsonObject { ["listen"] = $"127.0.0.1:{httpPort}" },
                ["logger"] = new JsonObject { ["level"] = "warn" }
            };

            if (!string.IsNullOrWhiteSpace(proxyUsername) && !string.IsNullOrEmpty(proxyPassword))
            {
                hysteriaConfig["socks5"]!["username"] = proxyUsername;
                hysteriaConfig["socks5"]!["password"] = proxyPassword;
                hysteriaConfig["http"]!["username"] = proxyUsername;
                hysteriaConfig["http"]!["password"] = proxyPassword;
            }

            if (rawHysteriaConfig["sni"] != null)
            {
                var tlsConfig = new JsonObject
                {
                    ["sni"] = rawHysteriaConfig["sni"]?.ToString(),
                    ["insecure"] = rawHysteriaConfig["insecure"]?.AsValue().GetValue<bool>() ?? false
                };

                var alpn = rawHysteriaConfig["alpn"]?.ToString();
                if (!string.IsNullOrWhiteSpace(alpn))
                {
                    tlsConfig["alpn"] = new JsonArray(alpn
                        .Split(',', StringSplitOptions.RemoveEmptyEntries)
                        .Select(value => JsonValue.Create(value.Trim()))
                        .ToArray());
                }

                hysteriaConfig["tls"] = tlsConfig;
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

            return hysteriaConfig.ToJsonString();
        }

        if (string.IsNullOrWhiteSpace(proxyUsername) || string.IsNullOrEmpty(proxyPassword))
            return parsedConfig;

        var coreConfig = JsonNode.Parse(parsedConfig)!.AsObject();
        if (coreConfig["inbounds"] is JsonArray inbounds)
        {
            foreach (var inbound in inbounds.OfType<JsonObject>())
            {
                if (inbound["protocol"]?.ToString() is not ("socks" or "http")) continue;
                var settings = inbound["settings"] as JsonObject ?? new JsonObject();
                inbound["settings"] = settings;
                if (inbound["protocol"]!.ToString() == "socks") settings["auth"] = "password";
                string accountsProperty = inbound["protocol"]!.ToString() == "socks" ? "accounts" : "users";
                settings[accountsProperty] = new JsonArray(new JsonObject
                {
                    ["user"] = proxyUsername,
                    ["pass"] = proxyPassword
                });
            }
        }
        return coreConfig.ToJsonString();
    }

    public static async Task<string> GetBoundedTextAsync(HttpClient client, string url)
    {
        using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxSubscriptionBytes)
            throw new InvalidDataException("Размер подписки превышает допустимый предел 1 МБ.");

        await using var input = await response.Content.ReadAsStreamAsync();
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            int count = await input.ReadAsync(buffer, 0, buffer.Length);
            if (count == 0) break;
            if (output.Length + count > MaxSubscriptionBytes)
                throw new InvalidDataException("Размер подписки превышает допустимый предел 1 МБ.");
            await output.WriteAsync(buffer, 0, count);
        }
        return Encoding.UTF8.GetString(output.ToArray());
    }

    public static bool IsSupportedShareLink(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim();
        return trimmed.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("vmess://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("naive+https://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("hysteria2://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("hy2://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("mieru://", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("mierus://", StringComparison.OrdinalIgnoreCase)
            || trimmed.Contains("type: mieru", StringComparison.Ordinal)
            || trimmed.Contains("type: \"mieru\"", StringComparison.Ordinal)
            || trimmed.Contains("type: 'mieru'", StringComparison.Ordinal);
    }
}
