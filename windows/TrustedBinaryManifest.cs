using System;
using System.Collections.Generic;

namespace Anarise
{
    public sealed class TrustedBinaryManifest
    {
        public Dictionary<string, TrustedBinaryArtifact> Artifacts { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    public sealed class TrustedBinaryArtifact
    {
        public string Url { get; set; } = "";
        public string Sha256 { get; set; } = "";
    }
}
