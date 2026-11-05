using System.Text.Json;
using System.IO;

namespace Anarise;

internal sealed class NetworkStateGuard
{
    private readonly string recoveryPath;
    private ProxySettingsSnapshot? snapshot;

    public NetworkStateGuard(string appDataPath)
    {
        recoveryPath = Path.Combine(appDataPath, "network-recovery.json");
    }

    public void RecoverInterruptedSession()
    {
        bool recovered = true;
        try
        {
            if (File.Exists(recoveryPath))
            {
                snapshot = JsonSerializer.Deserialize<ProxySettingsSnapshot>(File.ReadAllText(recoveryPath));
                recovered &= snapshot != null && SystemProxyManager.RestoreSettings(snapshot);
            }
        }
        catch { recovered = false; }

        snapshot = null;
        SystemProxyManager.SetChromiumQuicAllowed(true);
        recovered &= SystemProxyManager.SetBrowserQuicBlocked(false);
        recovered &= SystemProxyManager.SetIpv6Blocked(false);
        recovered &= SystemProxyManager.SetLanDnsBlocked(false);
        recovered &= SystemProxyManager.SetKillSwitchBlocked(false);
        if (recovered)
        {
            DeleteRecoveryFile();
        }
        else
            Console.WriteLine("Network recovery was incomplete; saved state retained for the next launch.");
    }

    public void Begin()
    {
        snapshot = SystemProxyManager.CaptureSettings();
        var temporaryPath = recoveryPath + ".tmp";
        File.WriteAllText(temporaryPath, JsonSerializer.Serialize(snapshot));
        File.Move(temporaryPath, recoveryPath, true);
    }

    public void Complete()
    {
        snapshot = null;
        DeleteRecoveryFile();
    }

    public void Restore()
    {
        bool restored = snapshot == null;
        try
        {
            if (snapshot == null && File.Exists(recoveryPath))
            {
                snapshot = JsonSerializer.Deserialize<ProxySettingsSnapshot>(File.ReadAllText(recoveryPath));
                restored = snapshot != null;
            }
            if (snapshot != null) restored = SystemProxyManager.RestoreSettings(snapshot);
        }
        finally
        {
            if (restored)
            {
                snapshot = null;
                DeleteRecoveryFile();
            }
            else
                Console.WriteLine("Network settings were not fully restored; saved state retained for the next launch.");
        }
    }

    private void DeleteRecoveryFile()
    {
        try { if (File.Exists(recoveryPath)) File.Delete(recoveryPath); } catch { }
    }
}
