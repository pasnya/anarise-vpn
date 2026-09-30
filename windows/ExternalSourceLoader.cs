using System.Net.Http.Headers;
using System.Net.Http;
using System.IO;
using System.Text;

namespace Anarise;

public sealed record SourceLoadResult(List<string> Links, int LoadedSources, int FailedSources);
public static class ExternalSourceLoader
{
    public static async Task<SourceLoadResult> FetchAsync(List<ExternalSource> sources, Action<string>? log = null)
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("AnariseVPN/1.4.16");
        client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
        var results = await Task.WhenAll(sources.Select(async source =>
        {
            foreach (string url in new[] { source.Url, source.MirrorUrl })
            {
                try
                {
                    using var timeout = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15));
                    using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                    response.EnsureSuccessStatusCode();
                    const int limit = 4 * 1024 * 1024;
                    if (response.Content.Headers.ContentLength > limit) throw new InvalidDataException("Source exceeds 4 MiB.");
                    await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                    using var output = new MemoryStream();
                    byte[] buffer = new byte[8192];
                    int read;
                    while ((read = await input.ReadAsync(buffer, timeout.Token)) > 0)
                    {
                        if (output.Length + read > limit) throw new InvalidDataException("Source exceeds 4 MiB.");
                        output.Write(buffer, 0, read);
                    }
                    var links = ExternalSources.ParseSubscription(Encoding.UTF8.GetString(output.ToArray()));
                    if (links.Count == 0) throw new InvalidDataException("No supported configurations.");
                    log?.Invoke($"{source.Name}: {links.Count} конфигураций" + (url == source.Url ? "" : " (зеркало GitHub)"));
                    return links;
                }
                catch (Exception ex)
                {
                    string reason = ex is OperationCanceledException ? "таймаут" : ex is InvalidDataException ? ex.Message : "ошибка HTTP/сети";
                    log?.Invoke($"{source.Name}: {reason}" + (url == source.Url ? "; проверяю зеркало" : ""));
                }
            }
            return new List<string>();
        }));
        int loaded = results.Count(r => r.Count > 0);
        return new SourceLoadResult(ExternalSources.Interleave(results), loaded, sources.Count - loaded);
    }
}
