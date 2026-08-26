using System.Diagnostics;

namespace Anarise;

public sealed class CoreProcessManager : IDisposable
{
    private Process? process;
    public Process? Current => process;

    public Process Start(ProcessStartInfo startInfo, Action<string> onOutput, Action<string> onError)
    {
        Stop();
        var started = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        started.OutputDataReceived += (_, args) => { if (args.Data != null) onOutput(args.Data); };
        started.ErrorDataReceived += (_, args) => { if (args.Data != null) onError(args.Data); };
        if (!started.Start())
        {
            started.Dispose();
            throw new InvalidOperationException("Не удалось запустить VPN-ядро.");
        }
        started.BeginOutputReadLine();
        started.BeginErrorReadLine();
        process = started;
        return started;
    }

    public void Stop()
    {
        var stopped = process;
        process = null;
        if (stopped == null) return;
        try
        {
            if (!stopped.HasExited)
            {
                stopped.Kill(true);
                stopped.WaitForExit(3000);
            }
        }
        catch { }
        finally { stopped.Dispose(); }
    }

    public void Dispose() => Stop();
}
