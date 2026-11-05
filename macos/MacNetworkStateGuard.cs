using System;
using System.IO;
using System.Text.Json;

namespace Anarise;

public sealed class MacSessionRecoveryState
{
    public bool ProxyActive { get; set; }
    public bool KillSwitchActive { get; set; }
    public bool TunRoutesActive { get; set; }
    public string? ServerIp { get; set; }
    public string Service { get; set; } = "Wi-Fi";
    public string[]? OriginalDnsServers { get; set; }
    public MacProxySettingsSnapshot? OriginalProxySettings { get; set; }
}

public static class MacNetworkStateGuard
{
    private static readonly string StateFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".anarise",
        "session-guard.json"
    );

    public static bool RecordSession(bool proxyActive, bool killSwitchActive, string service, bool tunRoutesActive = false, string? serverIp = null, string[]? originalDnsServers = null, MacProxySettingsSnapshot? originalProxySettings = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(StateFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            if (!string.IsNullOrEmpty(dir) && !OperatingSystem.IsWindows())
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var state = new MacSessionRecoveryState
            {
                ProxyActive = proxyActive,
                KillSwitchActive = killSwitchActive,
                TunRoutesActive = tunRoutesActive,
                ServerIp = serverIp,
                Service = service,
                OriginalDnsServers = originalDnsServers ?? MacProxyManager.GetDnsServers(service),
                OriginalProxySettings = originalProxySettings
            };
            var temporaryPath = StateFilePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state));
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporaryPath, StateFilePath, true);
            return true;
        }
        catch { return false; }
    }

    public static void ClearSession()
    {
        try
        {
            if (File.Exists(StateFilePath))
                File.Delete(StateFilePath);
        }
        catch { }
    }

    public static void RecoverPreviousSessionIfNeeded()
    {
        try
        {
            if (!File.Exists(StateFilePath))
                return;

            var json = File.ReadAllText(StateFilePath);
            var state = JsonSerializer.Deserialize<MacSessionRecoveryState>(json);
            if (state == null) return;

            Console.WriteLine("[MacNetworkStateGuard] Recovering from previous unclean exit...");

            bool recovered = true;
            if (state.OriginalProxySettings != null)
            {
                recovered &= MacProxyManager.RestoreSettings(state.OriginalProxySettings);
            }
            else if (state.ProxyActive)
            {
                recovered &= MacProxyManager.SetProxy(false);
            }

            if (state.TunRoutesActive)
            {
                recovered &= MacProxyManager.CleanupTunRoutes(state.ServerIp);
                recovered &= MacProxyManager.RestoreDnsServers(state.Service, state.OriginalDnsServers);
            }

            if (state.KillSwitchActive)
            {
                recovered &= MacProxyManager.SetKillSwitchBlocked(false);
            }

            if (recovered) ClearSession();
            else Console.WriteLine("[MacNetworkStateGuard] Recovery incomplete; saved state retained for the next launch.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MacNetworkStateGuard] Recovery error: {ex.Message}");
        }
    }
}
