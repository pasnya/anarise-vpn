using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

namespace Anarise;

public sealed class MacProxySettingsSnapshot
{
    public string Service { get; set; } = "Wi-Fi";
    public bool WebProxyEnabled { get; set; }
    public bool SecureWebProxyEnabled { get; set; }
    public bool SocksProxyEnabled { get; set; }
}

public static class MacProxyManager
{
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

    private static string? DetectActiveService()
    {
        try
        {
            // 1. Get primary default interface (e.g. en0)
            var routeOutput = RunCommand("/sbin/route", "-n get default");
            var ifaceMatch = Regex.Match(routeOutput, @"interface:\s+([a-zA-Z0-9]+)");
            if (!ifaceMatch.Success)
                return "Wi-Fi";

            var iface = ifaceMatch.Groups[1].Value.Trim();

            // 2. Find service name matching this device in networksetup
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

        try
        {
            var web = RunCommand("/usr/sbin/networksetup", $"-getwebproxy \"{service}\"");
            snapshot.WebProxyEnabled = web.Contains("Enabled: Yes");

            var secure = RunCommand("/usr/sbin/networksetup", $"-getsecurewebproxy \"{service}\"");
            snapshot.SecureWebProxyEnabled = secure.Contains("Enabled: Yes");

            var socks = RunCommand("/usr/sbin/networksetup", $"-getsocksfirewallproxy \"{service}\"");
            snapshot.SocksProxyEnabled = socks.Contains("Enabled: Yes");
        }
        catch
        {
        }

        return snapshot;
    }

    public static bool RestoreSettings(MacProxySettingsSnapshot snapshot)
    {
        var service = snapshot.Service;
        try
        {
            if (!snapshot.WebProxyEnabled)
                RunCommand("/usr/sbin/networksetup", $"-setwebproxystate \"{service}\" off");
            if (!snapshot.SecureWebProxyEnabled)
                RunCommand("/usr/sbin/networksetup", $"-setsecurewebproxystate \"{service}\" off");
            if (!snapshot.SocksProxyEnabled)
                RunCommand("/usr/sbin/networksetup", $"-setsocksfirewallproxystate \"{service}\" off");

            return true;
        }
        catch
        {
            return false;
        }
    }

    public static bool SetProxy(bool enabled, int socksPort = 20808, int httpPort = 20809, bool bypassLan = true)
    {
        var service = GetActiveService();

        try
        {
            if (enabled)
            {
                // Enable HTTP & HTTPS proxy
                RunCommand("/usr/sbin/networksetup", $"-setwebproxy \"{service}\" 127.0.0.1 {httpPort}");
                RunCommand("/usr/sbin/networksetup", $"-setsecurewebproxy \"{service}\" 127.0.0.1 {httpPort}");
                RunCommand("/usr/sbin/networksetup", $"-setsocksfirewallproxy \"{service}\" 127.0.0.1 {socksPort}");

                RunCommand("/usr/sbin/networksetup", $"-setwebproxystate \"{service}\" on");
                RunCommand("/usr/sbin/networksetup", $"-setsecurewebproxystate \"{service}\" on");
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

                string rules = $"block drop out quick proto {{ tcp, udp }} to ! {{ {allowed} }}\n";
                File.WriteAllText(confPath, rules);

                RunCommand("/sbin/pfctl", $"-a anarise_ks -f {confPath} -e");
            }
            else
            {
                RunCommand("/sbin/pfctl", "-a anarise_ks -F all");
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
}
