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
    private static readonly MacSessionRecoveryStore RecoveryStore = new(StateFilePath);

    public static bool RecordSession(bool proxyActive, bool killSwitchActive, string service, bool tunRoutesActive = false, string? serverIp = null, string[]? originalDnsServers = null, MacProxySettingsSnapshot? originalProxySettings = null)
    {
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
        return RecoveryStore.Save(state);
    }

    public static void ClearSession()
    {
        RecoveryStore.Clear();
    }

    public static void RecoverPreviousSessionIfNeeded()
    {
        bool recovered = RecoveryStore.Recover(state =>
        {
            Console.WriteLine("[MacNetworkStateGuard] Recovering from previous unclean exit...");

            bool restored = true;
            if (state.OriginalProxySettings != null)
            {
                restored &= MacProxyManager.RestoreSettings(state.OriginalProxySettings);
            }
            else if (state.ProxyActive)
            {
                restored &= MacProxyManager.SetProxy(false);
            }

            if (state.TunRoutesActive)
            {
                restored &= MacProxyManager.CleanupTunRoutes(state.ServerIp);
                restored &= MacProxyManager.RestoreDnsServers(state.Service, state.OriginalDnsServers);
            }

            if (state.KillSwitchActive)
            {
                restored &= MacProxyManager.SetKillSwitchBlocked(false);
            }
            return restored;
        });

        if (!recovered)
            Console.WriteLine("[MacNetworkStateGuard] Recovery incomplete; saved state retained for the next launch.");
    }
}

internal sealed class MacSessionRecoveryStore
{
    private readonly string stateFilePath;

    public MacSessionRecoveryStore(string stateFilePath) => this.stateFilePath = stateFilePath;

    public bool Save(MacSessionRecoveryState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(stateFilePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (!string.IsNullOrEmpty(dir) && OperatingSystem.IsMacOS())
                File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

            var temporaryPath = stateFilePath + ".tmp";
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(state));
            if (OperatingSystem.IsMacOS())
                File.SetUnixFileMode(temporaryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporaryPath, stateFilePath, true);
            return true;
        }
        catch { return false; }
    }

    public bool Recover(Func<MacSessionRecoveryState, bool> restore)
    {
        if (!File.Exists(stateFilePath)) return true;
        try
        {
            var state = JsonSerializer.Deserialize<MacSessionRecoveryState>(File.ReadAllText(stateFilePath));
            if (state == null || !restore(state)) return false;
            File.Delete(stateFilePath);
            return true;
        }
        catch { return false; }
    }

    public void Clear()
    {
        try { if (File.Exists(stateFilePath)) File.Delete(stateFilePath); }
        catch { }
    }
}
