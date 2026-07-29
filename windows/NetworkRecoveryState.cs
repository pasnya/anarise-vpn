using System;

namespace Anarise
{
    public sealed class NetworkRecoveryState
    {
        public ProxySettingsSnapshot? ProxySettings { get; set; }
        public string? ServerIp { get; set; }
        public string? DefaultGateway { get; set; }
        public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    }
}
