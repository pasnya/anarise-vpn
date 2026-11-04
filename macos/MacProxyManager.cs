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

                RunPrivilegedShell($"/sbin/pfctl -a anarise_ks -f {confPath} -e");
            }
            else
            {
                RunPrivilegedShell("/sbin/pfctl -a anarise_ks -F all");
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

    public static bool ConfigureTunRoutes(string utunName, string? serverIp, string? defaultGateway)
    {
        try
        {
            var cmds = new System.Collections.Generic.List<string>
            {
                $"/sbin/ifconfig {utunName} 10.0.85.1 10.0.85.1 netmask 255.255.255.0 up"
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

            string combined = string.Join(" && ", cmds);
            return RunPrivilegedShell(combined);
        }
        catch
        {
            return false;
        }
    }

    public static void CleanupTunRoutes(string? serverIp)
    {
        try
        {
            var cmds = new System.Collections.Generic.List<string>
            {
                "/sbin/route delete -net 0.0.0.0/1 >/dev/null 2>&1 || true",
                "/sbin/route delete -net 128.0.0.0/1 >/dev/null 2>&1 || true"
            };

            if (!string.IsNullOrWhiteSpace(serverIp))
            {
                cmds.Add($"/sbin/route delete -host {serverIp} >/dev/null 2>&1 || true");
            }

            RunPrivilegedShell(string.Join(" ; ", cmds));
        }
        catch { }
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
                RunCommand("/bin/sh", $"-c \"{shellCommand.Replace("\"", "\\\"")}\"");
                return true;
            }

            string escaped = shellCommand.Replace("\\", "\\\\").Replace("\"", "\\\"");
            using var proc = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "/usr/bin/osascript",
                    Arguments = $"-e \"do shell script \\\"{escaped}\\\" with administrator privileges\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            proc.Start();
            proc.WaitForExit(30000);
            return proc.ExitCode == 0;
        }
        catch
        {
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
