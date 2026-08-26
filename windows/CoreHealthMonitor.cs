using System.Diagnostics;

namespace Anarise;

public static class CoreHealthMonitor
{
    public static bool IsHealthy(Process? coreProcess, Process? tunProcess, bool requiresTun)
    {
        try
        {
            if (coreProcess == null || coreProcess.HasExited) return false;
            return !requiresTun || (tunProcess != null && !tunProcess.HasExited);
        }
        catch { return false; }
    }
}
