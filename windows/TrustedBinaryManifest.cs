using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Anarise
{
    public sealed class TrustedBinaryManifest
    {
        public Dictionary<string, TrustedBinaryArtifact> Artifacts { get; set; } = new(StringComparer.OrdinalIgnoreCase);

        public static TrustedBinaryManifest Load(string path)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Trusted binary manifest is missing.", path);

            var manifest = JsonSerializer.Deserialize<TrustedBinaryManifest>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("Trusted binary manifest is empty.");

            if (manifest.Artifacts.Count == 0)
                throw new InvalidDataException("Trusted binary manifest contains no artifacts.");

            foreach (var pair in manifest.Artifacts)
            {
                if (string.IsNullOrWhiteSpace(pair.Value.Url) || !Uri.TryCreate(pair.Value.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                    throw new InvalidDataException($"Artifact '{pair.Key}' must use an absolute HTTPS URL.");

                if (!Regex.IsMatch(pair.Value.Sha256 ?? "", "^[0-9a-fA-F]{64}$"))
                    throw new InvalidDataException($"Artifact '{pair.Key}' has an invalid SHA-256 checksum.");
            }

            return manifest;
        }

        public TrustedBinaryArtifact GetRequired(string name)
        {
            if (!Artifacts.TryGetValue(name, out var artifact))
                throw new InvalidDataException($"Artifact '{name}' is not present in the trusted binary manifest.");
            return artifact;
        }
    }

    public sealed class TrustedBinaryArtifact
    {
        public string Url { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }

    public static class BinaryIntegrity
    {
        public static void VerifySha256(string path, string expectedSha256)
        {
            if (!File.Exists(path))
                throw new FileNotFoundException("Downloaded artifact is missing.", path);

            using var stream = File.OpenRead(path);
            using var sha256 = SHA256.Create();
            var actual = Convert.ToHexString(sha256.ComputeHash(stream));
            if (!actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SHA-256 mismatch for '{Path.GetFileName(path)}'.");
        }
    }
}
