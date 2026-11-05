using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Anarise;

public sealed class MacProxySettingsSnapshot
{
    public string Service { get; set; } = "Wi-Fi";
    public bool WebProxyEnabled { get; set; }
    public bool SecureWebProxyEnabled { get; set; }
    public bool SocksProxyEnabled { get; set; }
    public string WebProxyServer { get; set; } = "";
    public int WebProxyPort { get; set; }
    public string SecureWebProxyServer { get; set; } = "";
    public int SecureWebProxyPort { get; set; }
    public string SocksProxyServer { get; set; } = "";
    public int SocksProxyPort { get; set; }
    public string[]? ProxyBypassDomains { get; set; }
    public string[]? OriginalDnsServers { get; set; }
}

public static class MacProxyManager
{
    internal static Func<string, string, bool>? CommandSuccessRunnerOverride { get; set; }
    private static readonly object SyncLock = new();
    private static string? cachedActiveService;

    public static string GetActiveService()
    {
        lock (SyncLock)
        {
            if (!string.IsNullOrEmpty(cachedActiveService))
                return cachedActiveService;

            cachedActiveService = DetectActiveService();
            return cachedActiveService ?? "Wi-Fi";
        }
    }

    public static (string InterfaceName, string? GatewayIp) GetDefaultRouteInfo()
    {
        try
        {
            var routeOutput = RunCommand("/sbin/route", "-n get default");
            var ifaceMatch = Regex.Match(routeOutput, @"interface:\s+([a-zA-Z0-9]+)");
            var gwMatch = Regex.Match(routeOutput, @"gateway:\s+([0-9\.]+)");

            string iface = ifaceMatch.Success ? ifaceMatch.Groups[1].Value.Trim() : "en0";
            string? gw = gwMatch.Success ? gwMatch.Groups[1].Value.Trim() : null;
            return (iface, gw);
        }
        catch
        {
            return ("en0", null);
        }
    }

    private static string? DetectActiveService()
    {
        try
        {
            var (iface, _) = GetDefaultRouteInfo();

            var orderOutput = RunCommand("/usr/sbin/networksetup", "-listnetworkserviceorder");
            var lines = orderOutput.Split('\n');
            string? currentService = null;

            foreach (var line in lines)
            {
                var serviceMatch = Regex.Match(line, @"\(\d+\)\s+(.+)");
                if (serviceMatch.Success)
                {
                    currentService = serviceMatch.Groups[1].Value.Trim();
                }
                else if (currentService != null && line.Contains($"Device: {iface}"))
                {
                    return currentService;
                }
            }
        }
        catch
        {
            // Fallback default
        }

        return "Wi-Fi";
    }

    public static MacProxySettingsSnapshot CaptureSettings()
    {
        var service = GetActiveService();
        var snapshot = new MacProxySettingsSnapshot { Service = service };
        var web = RunCommand("/usr/sbin/networksetup", $"-getwebproxy \"{service}\"");
        var secure = RunCommand("/usr/sbin/networksetup", $"-getsecurewebproxy \"{service}\"");
        var socks = RunCommand("/usr/sbin/networksetup", $"-getsocksfirewallproxy \"{service}\"");
        var bypass = RunCommand("/usr/sbin/networksetup", $"-getproxybypassdomains \"{service}\"");
        if (new[] { web, secure, socks, bypass }.Any(output => output.StartsWith("Error:", StringComparison.OrdinalIgnoreCase)))
            throw new IOException("Не удалось сохранить текущие настройки системного прокси macOS.");

        snapshot.WebProxyEnabled = web.Contains("Enabled: Yes");
        snapshot.SecureWebProxyEnabled = secure.Contains("Enabled: Yes");
        snapshot.SocksProxyEnabled = socks.Contains("Enabled: Yes");
        (snapshot.WebProxyServer, snapshot.WebProxyPort) = ParseProxyEndpoint(web);
        (snapshot.SecureWebProxyServer, snapshot.SecureWebProxyPort) = ParseProxyEndpoint(secure);
        (snapshot.SocksProxyServer, snapshot.SocksProxyPort) = ParseProxyEndpoint(socks);
        snapshot.ProxyBypassDomains = bypass.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(value => value.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value) && !value.Contains("aren't any", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        snapshot.OriginalDnsServers = GetDnsServers(service);

        return snapshot;
    }

    public static string[] GetDnsServers(string? service = null)
    {
        service ??= GetActiveService();
        try
        {
            var dnsOut = RunCommand("/usr/sbin/networksetup", $"-getdnsservers \"{service}\"");
            if (string.IsNullOrWhiteSpace(dnsOut) ||
                dnsOut.Contains("There aren't any DNS Servers", StringComparison.OrdinalIgnoreCase))
            {
                return Array.Empty<string>();
            }

            return dnsOut
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .Where(s => System.Net.IPAddress.TryParse(s, out _))
                .ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static bool RestoreDnsServers(string? service = null, string[]? originalDnsServers = null)
    {
        service ??= GetActiveService();
        try
        {
            string dnsArgs = (originalDnsServers != null && originalDnsServers.Length > 0)
                ? string.Join(" ", originalDnsServers)
                : "Empty";
            return RunCommandSucceeded("/usr/sbin/networksetup", $"-setdnsservers \"{service}\" {dnsArgs}") &&
                RunCommandSucceeded("/usr/bin/dscacheutil", "-flushcache");
        }
        catch
        {
            return false;
        }
    }

    public static bool RestoreSettings(MacProxySettingsSnapshot snapshot)
    {
        var service = snapshot.Service;
        try
        {
            bool restored = RestoreProxy(snapshot, "web", snapshot.WebProxyServer, snapshot.WebProxyPort, snapshot.WebProxyEnabled);
            restored &= RestoreProxy(snapshot, "secureweb", snapshot.SecureWebProxyServer, snapshot.SecureWebProxyPort, snapshot.SecureWebProxyEnabled);
            restored &= RestoreProxy(snapshot, "socksfirewall", snapshot.SocksProxyServer, snapshot.SocksProxyPort, snapshot.SocksProxyEnabled);
            var bypassArgs = snapshot.ProxyBypassDomains is { Length: > 0 }
                ? string.Join(" ", snapshot.ProxyBypassDomains.Select(value => $"\"{value.Replace("\"", "") }\""))
                : "Empty";
            restored &= RunCommandSucceeded("/usr/sbin/networksetup", $"-setproxybypassdomains \"{service}\" {bypassArgs}");

            restored &= RestoreDnsServers(service, snapshot.OriginalDnsServers);
            return restored;
        }
        catch
        {
            return false;
        }
    }

    private static (string Server, int Port) ParseProxyEndpoint(string output)
    {
        var server = Regex.Match(output, @"(?m)^Server:\s*(.+?)\s*$");
        var port = Regex.Match(output, @"(?m)^Port:\s*(\d+)\s*$");
        return (server.Success ? server.Groups[1].Value.Trim() : "", port.Success && int.TryParse(port.Groups[1].Value, out var value) ? value : 0);
    }

    private static bool RestoreProxy(MacProxySettingsSnapshot snapshot, string kind, string server, int port, bool enabled)
    {
        string service = snapshot.Service;
        string stateFlag = kind switch
        {
            "web" => "-setwebproxystate",
            "secureweb" => "-setsecurewebproxystate",
            _ => "-setsocksfirewallproxystate"
        };
        string setFlag = kind switch
        {
            "web" => "-setwebproxy",
            "secureweb" => "-setsecurewebproxy",
            _ => "-setsocksfirewallproxy"
        };

        bool ok = true;
        if (!string.IsNullOrWhiteSpace(server) && port is > 0 and <= 65535)
            ok &= RunCommandSucceeded("/usr/sbin/networksetup", $"{setFlag} \"{service}\" \"{server.Replace("\"", "") }\" {port} off");
        ok &= RunCommandSucceeded("/usr/sbin/networksetup", $"{stateFlag} \"{service}\" {(enabled ? "on" : "off")}");
        return ok;
    }

    public static bool SetProxy(bool enabled, int socksPort = 20808, int httpPort = 20809, bool bypassLan = true, bool enableHttpProxy = true, string? username = null, string? password = null)
    {
        var service = GetActiveService();

        try
        {
            if (enabled)
            {
                if (enableHttpProxy)
                {
                    string authArgs = !string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password)
                        ? $" on \"{username}\" \"{password}\"" : " off";
                    RunCommand("/usr/sbin/networksetup", $"-setwebproxy \"{service}\" 127.0.0.1 {httpPort}{authArgs}");
                    RunCommand("/usr/sbin/networksetup", $"-setsecurewebproxy \"{service}\" 127.0.0.1 {httpPort}{authArgs}");
                    RunCommand("/usr/sbin/networksetup", $"-setwebproxystate \"{service}\" on");
                    RunCommand("/usr/sbin/networksetup", $"-setsecurewebproxystate \"{service}\" on");
                }
                else
                {
                    RunCommand("/usr/sbin/networksetup", $"-setwebproxystate \"{service}\" off");
                    RunCommand("/usr/sbin/networksetup", $"-setsecurewebproxystate \"{service}\" off");
                }

                string socksAuthArgs = !string.IsNullOrWhiteSpace(username) && !string.IsNullOrEmpty(password)
                    ? $" on \"{username}\" \"{password}\"" : " off";
                RunCommand("/usr/sbin/networksetup", $"-setsocksfirewallproxy \"{service}\" 127.0.0.1 {socksPort}{socksAuthArgs}");
                RunCommand("/usr/sbin/networksetup", $"-setsocksfirewallproxystate \"{service}\" on");

                if (bypassLan)
                {
                    RunCommand("/usr/sbin/networksetup", $"-setproxybypassdomains \"{service}\" 127.0.0.1 localhost 192.168.0.0/16 10.0.0.0/8 172.16.0.0/12 *.local");
                }
            }
            else
            {
                RunCommand("/usr/sbin/networksetup", $"-setwebproxystate \"{service}\" off");
                RunCommand("/usr/sbin/networksetup", $"-setsecurewebproxystate \"{service}\" off");
                RunCommand("/usr/sbin/networksetup", $"-setsocksfirewallproxystate \"{service}\" off");
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MacProxyManager] Failed to set proxy: {ex.Message}");
            return false;
        }
    }

    public static bool SetKillSwitchBlocked(bool blocked, string? allowedServerIp = null)
    {
        try
        {
            string confPath = "/tmp/anarise_killswitch.pf";
            if (blocked)
            {
                var allowed = "127.0.0.1, 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16";
                if (!string.IsNullOrWhiteSpace(allowedServerIp) && allowedServerIp != "127.0.0.1")
                {
                    allowed += $", {allowedServerIp.Trim()}";
                }

                // Prevent DNS leak to LAN routers (192.168.x.1:53, 10.x.x.1:53) while Kill Switch is active
                string rules =
                    "block drop out quick proto { tcp, udp } to { 10.0.0.0/8, 172.16.0.0/12, 192.168.0.0/16 } port 53\n" +
                    $"block drop out quick proto {{ tcp, udp }} to ! {{ {allowed} }}\n";
                File.WriteAllText(confPath, rules);

                if (!RunPrivilegedShell($"/sbin/pfctl -a anarise_ks -f {confPath} -e")) return false;
            }
            else
            {
                if (!RunPrivilegedShell("/sbin/pfctl -a anarise_ks -F all")) return false;
                if (File.Exists(confPath))
                {
                    try { File.Delete(confPath); } catch { }
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MacProxyManager] Failed to set kill switch: {ex.Message}");
            return false;
        }
    }

    private static string[]? savedTunDnsServers;

    public static bool ConfigureTunRoutes(string utunName, string? serverIp, string? defaultGateway)
    {
        try
        {
            string service = GetActiveService();
            savedTunDnsServers ??= GetDnsServers(service);

            var cmds = new System.Collections.Generic.List<string>
            {
                $"/sbin/ifconfig {utunName} 10.0.85.1 10.0.85.1 netmask 255.255.255.0 mtu 1350 up"
            };

            if (!string.IsNullOrWhiteSpace(serverIp) && !string.IsNullOrWhiteSpace(defaultGateway))
            {
                cmds.Add($"/sbin/route delete -host {serverIp} >/dev/null 2>&1 || true");
                cmds.Add($"/sbin/route add -host {serverIp} {defaultGateway}");
            }

            cmds.Add("/sbin/route delete -net 0.0.0.0/1 >/dev/null 2>&1 || true");
            cmds.Add("/sbin/route delete -net 128.0.0.0/1 >/dev/null 2>&1 || true");
            cmds.Add("/sbin/route add -net 0.0.0.0/1 10.0.85.1");
            cmds.Add("/sbin/route add -net 128.0.0.0/1 10.0.85.1");
            // Switch macOS DNS to public resolvers routed inside the utun85 0.0.0.0/1 tunnel
            // so queries never bypass the tunnel to the local LAN router (e.g. 192.168.1.1).
            cmds.Add($"/usr/sbin/networksetup -setdnsservers \"{service}\" 1.1.1.1 8.8.8.8");
            cmds.Add("/usr/bin/dscacheutil -flushcache >/dev/null 2>&1 || true");
            cmds.Add("/usr/bin/killall -HUP mDNSResponder >/dev/null 2>&1 || true");

            string combined = string.Join(" && ", cmds);
            return RunPrivilegedShell(combined);
        }
        catch
        {
            return false;
        }
    }

    public static bool CleanupTunRoutes(string? serverIp)
    {
        try
        {
            string service = GetActiveService();
            string dnsRestoreArgs = (savedTunDnsServers != null && savedTunDnsServers.Length > 0)
                ? string.Join(" ", savedTunDnsServers)
                : "Empty";

            var cmds = new System.Collections.Generic.List<string>
            {
                "/sbin/route delete -net 0.0.0.0/1 >/dev/null 2>&1 || true",
                "/sbin/route delete -net 128.0.0.0/1 >/dev/null 2>&1 || true",
                $"/usr/sbin/networksetup -setdnsservers \"{service}\" {dnsRestoreArgs} >/dev/null 2>&1 || true",
                "/usr/bin/dscacheutil -flushcache >/dev/null 2>&1 || true"
            };

            if (!string.IsNullOrWhiteSpace(serverIp))
            {
                cmds.Add($"/sbin/route delete -host {serverIp} >/dev/null 2>&1 || true");
            }

            bool success = RunPrivilegedShell(string.Join(" ; ", cmds));
            savedTunDnsServers = null;
            return success;
        }
        catch { return false; }
    }

    public static bool SetAutostartLaunchAgent(bool enable)
    {
        try
        {
            string launchAgentsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "LaunchAgents");
            string plistPath = Path.Combine(launchAgentsDir, "com.anarise.vpn.plist");

            if (!enable)
            {
                if (File.Exists(plistPath))
                {
                    File.Delete(plistPath);
                }
                return true;
            }

            Directory.CreateDirectory(launchAgentsDir);
            string exePath = Process.GetCurrentProcess().MainModule?.FileName
                ?? Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Anarise");

            string plistContent = $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                    <key>Label</key>
                    <string>com.anarise.vpn</string>
                    <key>ProgramArguments</key>
                    <array>
                        <string>{System.Security.SecurityElement.Escape(exePath)}</string>
                    </array>
                    <key>RunAtLoad</key>
                    <true/>
                </dict>
                </plist>
                """;
            File.WriteAllText(plistPath, plistContent);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MacProxyManager] SetAutostartLaunchAgent error: {ex.Message}");
            return false;
        }
    }

    public static string? ReadClipboardText()
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/pbpaste",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            string text = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(2000);
            return text;
        }
        catch
        {
            return null;
        }
    }

    public static bool WriteClipboardText(string text)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/pbcopy",
                    RedirectStandardInput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            proc.StandardInput.Write(text);
            proc.StandardInput.Close();
            proc.WaitForExit(2000);
            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static bool RunPrivilegedShell(string shellCommand)
    {
        try
        {
            // Check if already root
            var idOut = RunCommand("/usr/bin/id", "-u").Trim();
            if (idOut == "0")
            {
                using var rootProc = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "/bin/sh",
                        ArgumentList = { "-c", shellCommand },
                        UseShellExecute = false,
                        CreateNoWindow = true
                    }
                };
                rootProc.Start();
                rootProc.WaitForExit(30000);
                return !rootProc.HasExited ? StopAndFail(rootProc) : rootProc.ExitCode == 0;
            }

            string escaped = shellCommand.Replace("\\", "\\\\").Replace("\"", "\\\"");
            string appleScript = $"do shell script \"{escaped}\" with administrator privileges";
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.StartInfo.ArgumentList.Add("-e");
            proc.StartInfo.ArgumentList.Add(appleScript);
            proc.Start();
            var standardOutput = proc.StandardOutput.ReadToEndAsync();
            var standardError = proc.StandardError.ReadToEndAsync();
            if (!proc.WaitForExit(120000))
            {
                proc.Kill(true);
                Console.Error.WriteLine("[MacProxyManager] Administrator authorization timed out.");
                return false;
            }
            Task.WaitAll(standardOutput, standardError);
            if (proc.ExitCode != 0)
            {
                string details = standardError.Result.Trim();
                Console.Error.WriteLine($"[MacProxyManager] Privileged command failed (osascript {proc.ExitCode}): {details}");
            }
            return proc.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[MacProxyManager] Privileged command could not be started: {ex.Message}");
            return false;
        }
    }

    public static string RunCommand(string binary, string arguments)
    {
        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = binary,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(3000);
            return output;
        }
        catch (Exception ex)
        {
            return $"Error: {ex.Message}";
        }
    }

    private static bool RunCommandSucceeded(string binary, string arguments)
    {
        if (CommandSuccessRunnerOverride != null)
            return CommandSuccessRunnerOverride(binary, arguments);

        try
        {
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = binary,
                    Arguments = arguments,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            _ = proc.StandardOutput.ReadToEnd();
            _ = proc.StandardError.ReadToEnd();
            proc.WaitForExit();
            return proc.ExitCode == 0;
        }
        catch { return false; }
    }

    private static bool StopAndFail(Process process)
    {
        try { process.Kill(true); } catch { }
        return false;
    }
}
