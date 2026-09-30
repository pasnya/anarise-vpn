namespace Anarise;

public sealed record ExternalScanCandidate(string Link, long Latency, ProbeStatus Status = ProbeStatus.Verified, DateTimeOffset? VerifiedAt = null);
public sealed record ExternalScanResult(List<ExternalScanCandidate> WorkingConfigs, int CheckedConfigs, int TrafficChecks)
{
    public Dictionary<ProbeStatus, int> ErrorCounts { get; init; } = new();
}

public static class ExternalConfigScanner
{
    public static async Task<ExternalScanResult> ScanAsync(
        IReadOnlyList<string> links,
        Func<string, Task<long>> measurePing,
        Func<string, Task<ServerProbeResult>> verifyTraffic,
        Action<int>? onPingProgress = null,
        Action<int>? onTrafficProgress = null,
        int configBatchSize = 200,
        int trafficBatchSize = 30,
        Action<ExternalScanCandidate>? onVerified = null)
    {
        if (configBatchSize <= 0 || trafficBatchSize <= 0)
            throw new ArgumentOutOfRangeException(nameof(configBatchSize), "Batch sizes must be positive.");

        int checkedConfigs = 0;
        int trafficChecks = 0;
        var working = new List<ExternalScanCandidate>();
        var errors = new System.Collections.Concurrent.ConcurrentDictionary<ProbeStatus, int>();
        using var pingSlots = new System.Threading.SemaphoreSlim(30);
        using var trafficSlots = new System.Threading.SemaphoreSlim(3);

        for (int offset = 0; offset < links.Count; offset += configBatchSize)
        {
            var reachable = new List<ExternalScanCandidate>();
            onPingProgress?.Invoke(checkedConfigs);
            var batch = links.Skip(offset).Take(configBatchSize);
            await Task.WhenAll(batch.Select(async link =>
            {
                await pingSlots.WaitAsync();
                try
                {
                    long latency = await measurePing(link);
                    // TCP is a ranking hint, never proof that a UDP/REALITY
                    // configuration is unusable. Failed hints are checked last.
                    lock (reachable) reachable.Add(new ExternalScanCandidate(link, latency));
                    int completed = System.Threading.Interlocked.Increment(ref checkedConfigs);
                    onPingProgress?.Invoke(completed);
                }
                finally { pingSlots.Release(); }
            }));

            var candidates = reachable.OrderBy(c => c.Latency < 0 ? long.MaxValue : c.Latency).ToList();
            for (int trafficOffset = 0; trafficOffset < candidates.Count; trafficOffset += trafficBatchSize)
            {
                onTrafficProgress?.Invoke(trafficChecks);
                var trafficBatch = candidates.Skip(trafficOffset).Take(trafficBatchSize);
                await Task.WhenAll(trafficBatch.Select(async candidate =>
                {
                    await trafficSlots.WaitAsync();
                    try
                    {
                        var result = await verifyTraffic(candidate.Link);
                        if (result.IsUsable)
                        {
                            var verified = new ExternalScanCandidate(candidate.Link, result.Latency, result.Status, result.VerifiedAt);
                            lock (working) working.Add(verified);
                            onVerified?.Invoke(verified);
                        }
                        else errors.AddOrUpdate(result.Status, 1, (_, count) => count + 1);
                    }
                    finally
                    {
                        int completed = System.Threading.Interlocked.Increment(ref trafficChecks);
                        onTrafficProgress?.Invoke(completed);
                        trafficSlots.Release();
                    }
                }));

                // Only stop after a batch supplies usable proxies. Otherwise
                // exhaust reachable candidates, then move to the next config batch.
                if (working.Count > 0 || errors.ContainsKey(ProbeStatus.Busy))
                    return new ExternalScanResult(working.OrderBy(c => c.Latency).ToList(), checkedConfigs, trafficChecks) { ErrorCounts = errors.ToDictionary(kv => kv.Key, kv => kv.Value) };
            }
        }

        return new ExternalScanResult(working, checkedConfigs, trafficChecks) { ErrorCounts = errors.ToDictionary(kv => kv.Key, kv => kv.Value) };
    }
}
