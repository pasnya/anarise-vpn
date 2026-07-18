using System;
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
        private static readonly string[] ChromiumPolicyPaths =
        {
            @"Software\Policies\Google\Chrome",
            @"Software\Policies\Microsoft\Edge"
        };
        private static readonly Dictionary<string, RegistryValueSnapshot?> SavedQuicPolicies = new();

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
