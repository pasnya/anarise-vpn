using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Anarise;

public enum ProbeStatus { Verified, Limited, InvalidConfig, MissingEngine, EngineStartFailed, Busy, Timeout, UnexpectedResponse, NetworkError }
public sealed record ServerProbeResult(ProbeStatus Status, long Latency = -1, DateTimeOffset? VerifiedAt = null)
{
    public bool IsUsable => Status is ProbeStatus.Verified or ProbeStatus.Limited;
    public string Label => Status switch
    {
        ProbeStatus.Verified => "Трафик подтверждён",
        ProbeStatus.Limited => "Частичная доступность",
        ProbeStatus.MissingEngine => "Не установлен VPN-модуль",
        ProbeStatus.EngineStartFailed => "Ошибка запуска VPN-модуля",
        ProbeStatus.InvalidConfig => "Ошибка конфигурации",
        ProbeStatus.Busy => "Проверка недоступна при активном VPN",
        ProbeStatus.Timeout => "Таймаут проверки",
        ProbeStatus.UnexpectedResponse => "Неверный ответ тестового сайта",
        _ => "Трафик не подтверждён"
    };
}

public sealed record ProbeTarget(string Url, int StatusCode, string? Body = null);

public sealed class ServerProbe
{
    public static readonly ProbeTarget[] Targets =
    {
        new("https://www.gstatic.com/generate_204", 204),
        new("https://cp.cloudflare.com/generate_204", 204),
        new("https://detectportal.firefox.com/success.txt", 200, "success")
    };
    private readonly string binariesPath;
    private readonly string dataPath;
    public ServerProbe(string binariesPath, string dataPath) { this.binariesPath = binariesPath; this.dataPath = dataPath; }

    public static bool IsExpectedResponse(ProbeTarget target, int code, string body) =>
        code == target.StatusCode && (target.Body == null ? string.IsNullOrEmpty(body) : body.Trim() == target.Body);

    public static ServerProbeResult? EvaluateSuccess(IReadOnlyCollection<string> targets, IReadOnlyList<long> delays, bool finalAttempt)
    {
        if (targets.Count >= 2)
            return new(ProbeStatus.Verified, delays.Order().ElementAt(delays.Count / 2), DateTimeOffset.UtcNow);
        if (finalAttempt && targets.Count == 1 && delays.Count >= 2)
            return new(ProbeStatus.Limited, delays.Order().ElementAt(delays.Count / 2), DateTimeOffset.UtcNow);
        return null;
    }

    public static JsonObject BuildCoreConfig(string parsedConfig, int socksPort, int rpcPort)
    {
        var source = JsonNode.Parse(parsedConfig)!.AsObject();
        if (LinkParser.IsMieruConfig(parsedConfig))
        {
            string host = source["server_host"]!.ToString();
            bool ip = IPAddress.TryParse(host, out _);
            return new JsonObject
            {
                ["profiles"] = new JsonArray(new JsonObject
                {
                    ["profileName"] = "verification",
                    ["user"] = new JsonObject { ["name"] = source["username"]!.ToString(), ["password"] = source["password"]!.ToString() },
                    ["servers"] = new JsonArray(new JsonObject
                    {
                        ["ipAddress"] = ip ? host : "", ["domainName"] = ip ? "" : host,
                        ["portBindings"] = new JsonArray(new JsonObject
                        {
                            ["port"] = source["server_port"]!.GetValue<int>(),
                            ["protocol"] = (source["transport"]?.ToString() ?? "TCP").ToUpperInvariant()
                        })
                    }),
                    ["mtu"] = 1350,
                    ["multiplexing"] = new JsonObject { ["level"] = (source["multiplexing"]?.ToString() ?? "MULTIPLEXING_LOW").ToUpperInvariant() },
                    ["handshakeMode"] = "HANDSHAKE_STANDARD"
                }),
                ["activeProfile"] = "verification", ["rpcPort"] = rpcPort,
                ["socks5Port"] = socksPort, ["socks5ListenLAN"] = false, ["loggingLevel"] = "ERROR"
            };
        }
        if (LinkParser.IsHysteria2Config(parsedConfig))
        {
            var tls = new JsonObject { ["sni"] = source["sni"]?.ToString() ?? "", ["insecure"] = source["insecure"]?.GetValue<bool>() ?? false };
            string alpn = source["alpn"]?.ToString() ?? "";
            if (!string.IsNullOrWhiteSpace(alpn)) tls["alpn"] = new JsonArray(alpn.Split(',').Select(s => JsonValue.Create(s.Trim())).ToArray());
            var config = new JsonObject
            {
                ["server"] = source["server"]!.ToString(), ["auth"] = source["auth"]!.ToString(), ["tls"] = tls,
                ["socks5"] = new JsonObject { ["listen"] = $"127.0.0.1:{socksPort}" },
                ["logger"] = new JsonObject { ["level"] = "error" }
            };
            if (source["obfs_type"]?.ToString() == "salamander")
                config["obfs"] = new JsonObject { ["type"] = "salamander", ["salamander"] = new JsonObject { ["password"] = source["obfs_password"]?.ToString() ?? "" } };
            return config;
        }
        // Every probe request must leave through the selected proxy, with no
        // private-address or DNS routing exception that could bypass it.
        source["routing"] = new JsonObject
        {
            ["domainStrategy"] = "AsIs",
            ["rules"] = new JsonArray(new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["outboundTag"] = "proxy" })
        };
        return source;
    }

    public async Task<ServerProbeResult> CheckAsync(string link)
    {
        Process? process = null;
        var reservations = new List<TcpListener>();
        string configPath = Path.Combine(dataPath, $"validation-{Guid.NewGuid():N}.json");
        try
        {
            for (int i = 0; i < 3; i++) { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); reservations.Add(listener); }
            int socksPort = ((IPEndPoint)reservations[0].LocalEndpoint).Port;
            int httpPort = ((IPEndPoint)reservations[1].LocalEndpoint).Port;
            int rpcPort = ((IPEndPoint)reservations[2].LocalEndpoint).Port;
            string parsed;
            JsonObject config;
            try { parsed = LinkParser.Parse(link, socksPort, httpPort); config = BuildCoreConfig(parsed, socksPort, rpcPort); }
            catch { return new(ProbeStatus.InvalidConfig); }
            bool mieru = LinkParser.IsMieruConfig(parsed);
            bool hysteria = LinkParser.IsHysteria2Config(parsed);
            string exe = Path.Combine(binariesPath, mieru ? "mieru.exe" : hysteria ? "hysteria.exe" : "xray.exe");
            if (!File.Exists(exe)) return new(ProbeStatus.MissingEngine);
            await File.WriteAllTextAsync(configPath, config.ToJsonString());
            var startInfo = new ProcessStartInfo
            {
                FileName = exe, Arguments = mieru ? "run" : hysteria ? $"client -c \"{configPath}\"" : $"run -c \"{configPath}\"",
                WorkingDirectory = binariesPath, CreateNoWindow = true, UseShellExecute = false,
                RedirectStandardOutput = true, RedirectStandardError = true
            };
            if (mieru) startInfo.Environment["MIERU_CONFIG_JSON_FILE"] = configPath;
            foreach (var listener in reservations) listener.Stop();
            try
            {
                process = Process.Start(startInfo);
                if (process == null) return new(ProbeStatus.EngineStartFailed);
                // Drain both pipes; a verbose core must not block on a full pipe.
                process.OutputDataReceived += (_, _) => { };
                process.ErrorDataReceived += (_, _) => { };
                process.BeginOutputReadLine(); process.BeginErrorReadLine();
            }
            catch { return new(ProbeStatus.EngineStartFailed); }
            var startup = Stopwatch.StartNew();
            bool ready = false;
            while (startup.Elapsed < TimeSpan.FromSeconds(5))
            {
                if (process.HasExited) return new(ProbeStatus.EngineStartFailed);
                try
                {
                    using var socket = new TcpClient();
                    using var timeout = new System.Threading.CancellationTokenSource(200);
                    await socket.ConnectAsync(IPAddress.Loopback, socksPort, timeout.Token);
                    ready = true; break;
                }
                catch { await Task.Delay(100); }
            }
            if (!ready) return new(ProbeStatus.EngineStartFailed);
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{socksPort}"), UseProxy = true,
                AllowAutoRedirect = false, UseCookies = false
            };
            using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(6) };
            client.DefaultRequestHeaders.CacheControl = new() { NoCache = true, NoStore = true };
            var passed = new HashSet<string>();
            var delays = new List<long>();
            bool timedOut = false, unexpected = false;
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var results = await Task.WhenAll(Targets.Select(async target =>
                {
                    var timer = Stopwatch.StartNew();
                    try
                    {
                        using var request = new HttpRequestMessage(HttpMethod.Get, target.Url);
                        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
                        using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(6));
                        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                        var bytes = new byte[65];
                        int total = 0, read;
                        while (total < bytes.Length && (read = await stream.ReadAsync(bytes.AsMemory(total), timeout.Token)) > 0) total += read;
                        string body = System.Text.Encoding.UTF8.GetString(bytes, 0, total);
                        bool ok = total < bytes.Length && IsExpectedResponse(target, (int)response.StatusCode, body);
                        return (target.Url, Ok: ok, Delay: timer.ElapsedMilliseconds, Timeout: false, Unexpected: !ok);
                    }
                    catch (OperationCanceledException) { return (target.Url, Ok: false, Delay: -1L, Timeout: true, Unexpected: false); }
                    catch { return (target.Url, Ok: false, Delay: -1L, Timeout: false, Unexpected: false); }
                }));
                foreach (var result in results)
                {
                    timedOut |= result.Timeout; unexpected |= result.Unexpected;
                    if (result.Ok) { passed.Add(result.Url); delays.Add(result.Delay); }
                }
                var success = EvaluateSuccess(passed, delays, attempt == 1);
                if (success != null) return success;
                if (process.HasExited) return new(ProbeStatus.EngineStartFailed);
                await Task.Delay(150);
            }
            return new(timedOut ? ProbeStatus.Timeout : unexpected ? ProbeStatus.UnexpectedResponse : ProbeStatus.NetworkError);
        }
        catch { return new(ProbeStatus.EngineStartFailed); }
        finally
        {
            foreach (var listener in reservations) listener.Stop();
            try { if (process != null && !process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } } catch { }
            process?.Dispose();
            try { File.Delete(configPath); } catch { }
        }
    }
}
