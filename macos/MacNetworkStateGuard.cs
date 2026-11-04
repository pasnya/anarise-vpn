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
}

public static class MacNetworkStateGuard
{
    private static readonly string StateFilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".anarise",
        "session-guard.json"
    );

    public static void RecordSession(bool proxyActive, bool killSwitchActive, string service, bool tunRoutesActive = false, string? serverIp = null)
    {
        try
        {
            var dir = Path.GetDirectoryName(StateFilePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);

            var state = new MacSessionRecoveryState
            {
                ProxyActive = proxyActive,
                KillSwitchActive = killSwitchActive,
                TunRoutesActive = tunRoutesActive,
                ServerIp = serverIp,
                Service = service
            };
            File.WriteAllText(StateFilePath, JsonSerializer.Serialize(state));
        }
        catch { }
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

            if (state.ProxyActive)
            {
                MacProxyManager.SetProxy(false);
            }

            if (state.TunRoutesActive)
            {
                MacProxyManager.CleanupTunRoutes(state.ServerIp);
            }

            if (state.KillSwitchActive)
            {
                MacProxyManager.SetKillSwitchBlocked(false);
            }

            ClearSession();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[MacNetworkStateGuard] Recovery error: {ex.Message}");
        }
    }
}
