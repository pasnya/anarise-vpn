using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace Anarise;

public sealed record UpdateCheckResult(string LatestVersion, string DownloadUrl);

public static class UpdateVersionHelper
{
    public static string NormalizeTag(string? rawTag)
    {
        if (string.IsNullOrWhiteSpace(rawTag))
            return string.Empty;

        string clean = rawTag.Trim();
        while (clean.StartsWith("v", StringComparison.OrdinalIgnoreCase))
        {
            clean = clean.Substring(1).Trim();
        }

        int suffixIdx = clean.IndexOfAny(new[] { '-', '+' });
        if (suffixIdx >= 0)
        {
            clean = clean.Substring(0, suffixIdx).Trim();
        }

        return clean;
    }

    public static bool TryParseVersion(string? rawTag, out int[] parts)
    {
        parts = Array.Empty<int>();
        string clean = NormalizeTag(rawTag);
        if (string.IsNullOrEmpty(clean))
            return false;

        string[] segments = clean.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return false;

        var parsed = new List<int>(Math.Max(segments.Length, 3));
        foreach (string seg in segments)
        {
            if (!int.TryParse(seg, out int num) || num < 0)
                return false;
            parsed.Add(num);
        }

        while (parsed.Count < 3)
        {
            parsed.Add(0);
        }

        parts = parsed.ToArray();
        return true;
    }

    public static int CompareVersions(string? candidateTag, string? referenceTag)
    {
        if (!TryParseVersion(candidateTag, out int[] candidate) ||
            !TryParseVersion(referenceTag, out int[] reference))
        {
            return 0;
        }

        int len = Math.Max(candidate.Length, reference.Length);
        for (int i = 0; i < len; i++)
        {
            int c = i < candidate.Length ? candidate[i] : 0;
            int r = i < reference.Length ? reference[i] : 0;
            if (c != r)
                return c.CompareTo(r);
        }

        return 0;
    }

    public static bool IsNewerVersion(string? candidateTag, string? currentVersion)
    {
        return CompareVersions(candidateTag, currentVersion) > 0;
    }

    public static UpdateCheckResult? FindNewerRelease(string releasesJson, string currentVersion, string platform = "windows")
    {
        if (string.IsNullOrWhiteSpace(releasesJson))
            return null;

        JsonNode? root;
        try
        {
            root = JsonNode.Parse(releasesJson);
        }
        catch
        {
            return null;
        }

        var releaseNodes = new List<JsonNode>();
        if (root is JsonArray array)
        {
            foreach (var item in array)
            {
                if (item != null)
                    releaseNodes.Add(item);
            }
        }
        else if (root is JsonObject obj)
        {
            releaseNodes.Add(obj);
        }

        JsonNode? bestRelease = null;
        string bestVersion = currentVersion;

        foreach (var rel in releaseNodes)
        {
            bool isDraft = rel["draft"]?.GetValue<bool>() ?? false;
            bool isPrerelease = rel["prerelease"]?.GetValue<bool>() ?? false;
            if (isDraft || isPrerelease)
                continue;

            string tag = NormalizeTag(rel["tag_name"]?.ToString());
            if (string.IsNullOrEmpty(tag))
                continue;

            if (IsNewerVersion(tag, bestVersion))
            {
                bestVersion = tag;
                bestRelease = rel;
            }
        }

        if (bestRelease == null)
            return null;

        string downloadUrl = SelectAssetDownloadUrl(bestRelease, platform);
        return new UpdateCheckResult(bestVersion, downloadUrl);
    }

    public static string SelectAssetDownloadUrl(JsonNode releaseNode, string platform)
    {
        string fallbackUrl = releaseNode["html_url"]?.ToString() ?? "https://github.com/pasnya/anarise-vpn/releases/latest";
        var assets = releaseNode["assets"]?.AsArray();
        if (assets == null || assets.Count == 0)
            return fallbackUrl;

        var validAssets = assets
            .Where(a => a != null && !string.IsNullOrWhiteSpace(a["name"]?.ToString()) && !string.IsNullOrWhiteSpace(a["browser_download_url"]?.ToString()))
            .Select(a => (Name: a!["name"]!.ToString(), Url: a["browser_download_url"]!.ToString()))
            .ToList();

        if (validAssets.Count == 0)
            return fallbackUrl;

        if (string.Equals(platform, "macos", StringComparison.OrdinalIgnoreCase))
        {
            var macDmg = validAssets.FirstOrDefault(a =>
                a.Name.Contains("macOS", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".dmg", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(macDmg.Url))
                return macDmg.Url;

            var macAny = validAssets.FirstOrDefault(a =>
                a.Name.Contains("macOS", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(macAny.Url))
                return macAny.Url;
        }
        else if (string.Equals(platform, "android", StringComparison.OrdinalIgnoreCase))
        {
            var apkAsset = validAssets.FirstOrDefault(a =>
                a.Name.EndsWith(".apk", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(apkAsset.Url))
                return apkAsset.Url;
        }
        else
        {
            var winAsset = validAssets.FirstOrDefault(a =>
                a.Name.Contains("Windows", StringComparison.OrdinalIgnoreCase) &&
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(winAsset.Url))
                return winAsset.Url;

            var winAny = validAssets.FirstOrDefault(a =>
                a.Name.Contains("Windows", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(winAny.Url))
                return winAny.Url;

            var nonMacZip = validAssets.FirstOrDefault(a =>
                a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) &&
                !a.Name.Contains("macOS", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(nonMacZip.Url))
                return nonMacZip.Url;
        }

        return fallbackUrl;
    }
}
