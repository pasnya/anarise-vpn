using System.Text;
using System.IO;
using System.Text.Json;

namespace Anarise;

public sealed record ExternalSource(string Name, string Url, int UpdateMinutes)
{
    public string MirrorUrl
    {
        get
        {
            var parts = new Uri(Url).AbsolutePath.Trim('/').Split('/');
            return $"https://cdn.jsdelivr.net/gh/{parts[0]}/{parts[1]}@{parts[2]}/{string.Join('/', parts.Skip(3))}";
        }
    }
}

public static class ExternalSources
{
    public static List<ExternalSource> Load(string path)
    {
        using var json = JsonDocument.Parse(File.ReadAllText(path));
        return json.RootElement.GetProperty("sources").EnumerateArray().Select(s =>
        {
            string url = s.GetProperty("url").GetString()!;
            var uri = new Uri(url);
            if (uri.Scheme != "https" || uri.Host != "raw.githubusercontent.com" || uri.Segments.Length < 5)
                throw new InvalidDataException("Invalid external source URL.");
            return new ExternalSource(s.GetProperty("name").GetString()!, url, s.GetProperty("updateMinutes").GetInt32());
        }).ToList();
    }

    public static List<string> ParseSubscription(string content)
    {
        string decoded = content.Trim().TrimStart('\uFEFF');
        if (!decoded.Contains("://"))
        {
            try
            {
                string base64 = string.Concat(decoded.Where(c => !char.IsWhiteSpace(c))).Replace('-', '+').Replace('_', '/');
                decoded = Encoding.UTF8.GetString(Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '=')));
            }
            catch (FormatException) { return new List<string>(); }
        }
        return decoded.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim()).Where(s => new[] { "vless://", "vmess://", "naive+https://", "hysteria2://", "hy2://", "mieru://", "mierus://" }
            .Any(prefix => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    public static List<string> Interleave(IEnumerable<List<string>> sources)
    {
        var lists = sources.ToList();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();
        for (int i = 0; i < lists.Select(s => s.Count).DefaultIfEmpty(0).Max(); i++)
            foreach (var list in lists)
                if (i < list.Count && seen.Add(list[i].Split('#')[0])) result.Add(list[i]);
        return result;
    }
}
