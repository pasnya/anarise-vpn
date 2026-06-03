using System;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Anarise
{
    public static class SystemProxyManager
    {
        [DllImport("wininet.dll", SetLastError = true)]
        private static extern bool InternetSetOption(IntPtr hInternet, int dwOption, IntPtr lpBuffer, int dwBufferLength);

        private const int INTERNET_OPTION_SETTINGS_CHANGED = 39;
        private const int INTERNET_OPTION_REFRESH = 37;

        private const string REG_KEY_PATH = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

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
