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
        try
        {
            if (!File.Exists(recoveryPath)) return;
            snapshot = JsonSerializer.Deserialize<ProxySettingsSnapshot>(File.ReadAllText(recoveryPath));
            if (snapshot != null) SystemProxyManager.RestoreSettings(snapshot);
        }
        catch { }
        finally
        {
            snapshot = null;
            DeleteRecoveryFile();
            SystemProxyManager.SetChromiumQuicAllowed(true);
            SystemProxyManager.SetBrowserQuicBlocked(false);
            SystemProxyManager.SetIpv6Blocked(false);
        }
    }

    public void Begin()
    {
        snapshot = SystemProxyManager.CaptureSettings();
        File.WriteAllText(recoveryPath, JsonSerializer.Serialize(snapshot));
    }

    public void Complete()
    {
        snapshot = null;
        DeleteRecoveryFile();
    }

    public void Restore()
    {
        try
        {
            if (snapshot != null) SystemProxyManager.RestoreSettings(snapshot);
        }
        finally
        {
            snapshot = null;
            DeleteRecoveryFile();
        }
    }

    private void DeleteRecoveryFile()
    {
        try { if (File.Exists(recoveryPath)) File.Delete(recoveryPath); } catch { }
    }
}
