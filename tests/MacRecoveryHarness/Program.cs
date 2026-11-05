using Anarise;

var temporaryDirectory = Path.Combine(Path.GetTempPath(), "anarise-mac-recovery-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);

try
{
    TestProxySettingsRestore();
    TestRecoveryRetryAndPersistence(temporaryDirectory);
    Console.WriteLine("macOS recovery checks passed.");
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
finally
{
    MacProxyManager.CommandSuccessRunnerOverride = null;
    try { Directory.Delete(temporaryDirectory, recursive: true); } catch { }
}

static void TestProxySettingsRestore()
{
    var commands = new List<string>();
    MacProxyManager.CommandSuccessRunnerOverride = (binary, arguments) =>
    {
        commands.Add(binary + " " + arguments);
        return true;
    };

    var snapshot = new MacProxySettingsSnapshot
    {
        Service = "Wi-Fi",
        WebProxyEnabled = true,
        WebProxyServer = "old-web-proxy.example",
        WebProxyPort = 8080,
        SecureWebProxyEnabled = false,
        SecureWebProxyServer = "old-secure-proxy.example",
        SecureWebProxyPort = 8443,
        SocksProxyEnabled = true,
        SocksProxyServer = "old-socks-proxy.example",
        SocksProxyPort = 1080,
        ProxyBypassDomains = new[] { "localhost", "*.internal" },
        OriginalDnsServers = new[] { "9.9.9.9", "149.112.112.112" }
    };

    Require(MacProxyManager.RestoreSettings(snapshot), "RestoreSettings should report success when each OS command succeeds.");
    Require(commands.Any(command => command.Contains("-setwebproxy \"Wi-Fi\" \"old-web-proxy.example\" 8080 off", StringComparison.Ordinal)),
        "The original HTTP proxy endpoint was not restored.");
    Require(commands.Any(command => command.Contains("-setwebproxystate \"Wi-Fi\" on", StringComparison.Ordinal)),
        "The original HTTP proxy enabled state was not restored.");
    Require(commands.Any(command => command.Contains("-setsocksfirewallproxy \"Wi-Fi\" \"old-socks-proxy.example\" 1080 off", StringComparison.Ordinal)),
        "The original SOCKS proxy endpoint was not restored.");
    Require(commands.Any(command => command.Contains("-setsocksfirewallproxystate \"Wi-Fi\" on", StringComparison.Ordinal)),
        "The original SOCKS proxy enabled state was not restored.");
    Require(commands.Any(command => command.Contains("-setproxybypassdomains \"Wi-Fi\" \"localhost\" \"*.internal\"", StringComparison.Ordinal)),
        "The original proxy bypass domains were not restored.");
    Require(commands.Any(command => command.Contains("-setdnsservers \"Wi-Fi\" 9.9.9.9 149.112.112.112", StringComparison.Ordinal)),
        "The original DNS servers were not restored.");

    MacProxyManager.CommandSuccessRunnerOverride = (_, _) => false;
    Require(!MacProxyManager.RestoreSettings(snapshot), "RestoreSettings must report a failed OS command.");
}

static void TestRecoveryRetryAndPersistence(string temporaryDirectory)
{
    string statePath = Path.Combine(temporaryDirectory, "session-guard.json");
    var store = new MacSessionRecoveryStore(statePath);
    var originalProxy = new MacProxySettingsSnapshot
    {
        Service = "Ethernet",
        WebProxyServer = "proxy.internal",
        WebProxyPort = 3128,
        WebProxyEnabled = true,
        OriginalDnsServers = new[] { "1.1.1.1" }
    };
    var session = new MacSessionRecoveryState
    {
        ProxyActive = true,
        TunRoutesActive = true,
        ServerIp = "198.51.100.10",
        Service = "Ethernet",
        OriginalDnsServers = new[] { "1.1.1.1" },
        OriginalProxySettings = originalProxy
    };

    Require(store.Save(session), "Recovery state could not be saved.");
    Require(!store.Recover(_ => false), "A failed recovery must report failure.");
    Require(File.Exists(statePath), "Recovery state was deleted after an unsuccessful restore.");

    bool receivedOriginalState = false;
    Require(store.Recover(state =>
    {
        receivedOriginalState = state.TunRoutesActive && state.ServerIp == "198.51.100.10" &&
            state.OriginalProxySettings?.WebProxyPort == 3128 && state.OriginalDnsServers?.Single() == "1.1.1.1";
        return receivedOriginalState;
    }), "A successful recovery should clear its saved state.");
    Require(receivedOriginalState, "The saved network state did not round-trip completely.");
    Require(!File.Exists(statePath), "Recovery state was not cleared after successful restoration.");
}

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
