namespace Anarise;

public readonly record struct TrafficSnapshot(long UploadSpeed, long DownloadSpeed, long TotalUpload, long TotalDownload);

public sealed class StatisticsService
{
    private long lastSent = -1;
    private long lastReceived = -1;

    public TrafficSnapshot Update(long bytesSent, long bytesReceived)
    {
        var upload = lastSent >= 0 ? Math.Max(0, bytesSent - lastSent) : 0;
        var download = lastReceived >= 0 ? Math.Max(0, bytesReceived - lastReceived) : 0;
        lastSent = bytesSent;
        lastReceived = bytesReceived;
        return new TrafficSnapshot(upload, download, upload, download);
    }

    public void Reset()
    {
        lastSent = -1;
        lastReceived = -1;
    }
}
