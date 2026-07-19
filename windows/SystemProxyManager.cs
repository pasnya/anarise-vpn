using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Anarise
{
    public static class SystemProxyManager
    {
        private sealed class RegistryValueSnapshot
        {
            public object Value { get; init; }
            public RegistryValueKind Kind { get; init; }
        }

        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        private const string REG_KEY_PATH = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";
        private const string QUIC_POLICY_VALUE = "QuicAllowed";
        private const string QUIC_FIREWALL_GROUP = "Anarise VPN QUIC";
        private const string IPV6_FIREWALL_GROUP = "Anarise VPN IPv6";
        private static readonly string[] ChromiumPolicyPaths =
        {
            @"Software\Policies\Google\Chrome",
            @"Software\Policies\Microsoft\Edge"
        };
        private static readonly Dictionary<string, RegistryValueSnapshot?> SavedQuicPolicies = new();

        private static readonly HashSet<string> BrowserProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "chrome", "msedge", "brave", "vivaldi", "opera", "opera_gx", "firefox",
            "chromium", "yandex", "browser", "arc"
        };

        public static void SetProxy(bool enabled, string server = "127.0.0.1:20809", bool bypassLan = true)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(REG_KEY_PATH, true))
                {
                    if (key != null)
                    {
                        if (enabled)
                        {
                            key.SetValue("ProxyEnable", 1, RegistryValueKind.DWord);
                            // We can route both http and https through our local http inbound port 20809
                            key.SetValue("ProxyServer", server, RegistryValueKind.String);

                            if (bypassLan)
                            {
                                // Standard local IP ranges to bypass
                                string bypass = "localhost;127.0.0.1;192.168.*;10.*;172.16.*;172.17.*;172.18.*;172.19.*;172.20.*;172.21.*;172.22.*;172.23.*;172.24.*;172.25.*;172.26.*;172.27.*;172.28.*;172.29.*;172.30.*;172.31.*;<local>";
                                key.SetValue("ProxyOverride", bypass, RegistryValueKind.String);
                            }
                            else
                            {
                                key.SetValue("ProxyOverride", "localhost;127.0.0.1;<local>", RegistryValueKind.String);
                            }
                        }
                        else
                        {
                            key.SetValue("ProxyEnable", 0, RegistryValueKind.DWord);
                        }
                    }
                }

                // Delete connection setting caches to force Windows to regenerate DefaultConnectionSettings
                try
                {
                    using (RegistryKey connKey = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Internet Settings\Connections", true))
                    {
                        if (connKey != null)
                        {
                            connKey.DeleteValue("DefaultConnectionSettings", false);
                            connKey.DeleteValue("SavedLegacySettings", false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to clear connection settings caches: " + ex.Message);
                }

                // Notify IE / Windows that settings have changed
                NotifySystem();
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to update system proxy registry settings: " + ex.Message);
            }
        }

        public static void DisableProxy()
        {
            SetProxy(false);
        }

        // HTTP system proxies do not tunnel QUIC/HTTP3 UDP traffic. Disable QUIC
        // only while the proxy is active, then put the user's policy back exactly
        // as it was before the connection.
        public static void SetChromiumQuicAllowed(bool allowed)
        {
            foreach (var policyPath in ChromiumPolicyPaths)
            {
                try
                {
                    using var key = Registry.CurrentUser.CreateSubKey(policyPath, true);
                    if (key == null) continue;

                    if (!allowed)
                    {
                        if (!SavedQuicPolicies.ContainsKey(policyPath))
                        {
                            var existing = key.GetValue(QUIC_POLICY_VALUE);
                            if (existing != null)
                            {
                                SavedQuicPolicies[policyPath] = new RegistryValueSnapshot
                                {
                                    Value = existing,
                                    Kind = key.GetValueKind(QUIC_POLICY_VALUE)
                                };
                            }
                            else
                            {
                                SavedQuicPolicies[policyPath] = null;
                            }
                        }

                        key.SetValue(QUIC_POLICY_VALUE, 0, RegistryValueKind.DWord);
                    }
                    else if (SavedQuicPolicies.Remove(policyPath, out var saved))
                    {
                        if (saved != null)
                        {
                            key.SetValue(QUIC_POLICY_VALUE, saved.Value, saved.Kind);
                        }
                        else
                        {
                            key.DeleteValue(QUIC_POLICY_VALUE, false);
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Failed to update Chromium QUIC policy: " + ex.Message);
                }
            }
        }

        // Chromium policies are not guaranteed to be applied to an already running
        // browser. Firewall rules make the QUIC fallback immediate and also cover
        // Firefox and Chromium-based browsers other than Chrome/Edge.
        public static bool SetBrowserQuicBlocked(bool blocked)
        {
            DeleteFirewallGroup(QUIC_FIREWALL_GROUP);
            if (!blocked) return true;

            int ruleIndex = 0;
            bool succeeded = false;
            foreach (var browserPath in FindBrowserExecutables())
            {
                succeeded |= RunNetsh($"advfirewall firewall add rule name=\"Anarise VPN QUIC {++ruleIndex}\" group=\"{QUIC_FIREWALL_GROUP}\" dir=out action=block protocol=UDP remoteport=443 program=\"{browserPath}\" enable=yes profile=any");
            }
            return succeeded;
        }

        // The tunnel is intentionally IPv4-only. Blocking IPv6 while connected
        // prevents Windows from bypassing the IPv4 TUN/default-proxy path.
        public static bool SetIpv6Blocked(bool blocked)
        {
            DeleteFirewallGroup(IPV6_FIREWALL_GROUP);
            if (!blocked) return true;

            return RunNetsh($"advfirewall firewall add rule name=\"Anarise VPN - Block IPv6\" group=\"{IPV6_FIREWALL_GROUP}\" dir=out action=block remoteip=::/0 enable=yes profile=any");
        }

        private static IEnumerable<string> FindBrowserExecutables()
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            string programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            string[] candidates =
            {
                Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFilesX86, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"),
                Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"),
                Path.Combine(programFiles, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(programFilesX86, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(localAppData, "BraveSoftware", "Brave-Browser", "Application", "brave.exe"),
                Path.Combine(programFiles, "Vivaldi", "Application", "vivaldi.exe"),
                Path.Combine(localAppData, "Vivaldi", "Application", "vivaldi.exe"),
                Path.Combine(programFiles, "Mozilla Firefox", "firefox.exe"),
                Path.Combine(programFilesX86, "Mozilla Firefox", "firefox.exe"),
                Path.Combine(localAppData, "Programs", "Opera", "opera.exe"),
                Path.Combine(localAppData, "Programs", "Opera GX", "opera.exe"),
                Path.Combine(localAppData, "Yandex", "YandexBrowser", "Application", "browser.exe")
            };

            foreach (var path in candidates.Where(File.Exists)) paths.Add(path);

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    if (!BrowserProcessNames.Contains(process.ProcessName)) continue;
                    var path = process.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path) && File.Exists(path)) paths.Add(path);
                }
                catch { }
                finally { process.Dispose(); }
            }

            return paths;
        }

        private static void DeleteFirewallGroup(string group)
        {
            RunNetsh($"advfirewall firewall delete rule name=all group=\"{group}\"");
        }

        private static bool RunNetsh(string arguments)
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = "netsh",
                    Arguments = arguments,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (process == null) return false;
                if (!process.WaitForExit(5000))
                {
                    try { process.Kill(true); } catch { }
                    return false;
                }
                return process.ExitCode == 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("Failed to update VPN firewall rules: " + ex.Message);
                return false;
            }
        }

        private static void NotifySystem()
        {
            // Allocate memory for options
            IntPtr buffer = Marshal.AllocCoTaskMem(0);
            try
            {
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_SETTINGS_CHANGED, IntPtr.Zero, 0);
                InternetSetOption(IntPtr.Zero, INTERNET_OPTION_REFRESH, IntPtr.Zero, 0);
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }
    }
}
