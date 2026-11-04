using System.Text;
using System.Text.Json;
using Anarise;

if (args.Contains("--probe-tests")) return await RunProbeTests();
if (args.Contains("--external-live-check"))
{
    var sources = ExternalSources.Load(Path.Combine(Directory.GetCurrentDirectory(), "shared", "external-sources.json"));
    var loaded = await ExternalSourceLoader.FetchAsync(sources, Console.WriteLine);
    Console.WriteLine($"Sources={loaded.LoadedSources}/{sources.Count}, unique configurations={loaded.Links.Count}");
    var probe = new ServerProbe(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnariseVPN", "binaries"), Path.GetTempPath());
    var results = new System.Collections.Concurrent.ConcurrentBag<ServerProbeResult>();
    await Parallel.ForEachAsync(loaded.Links.Take(30), new ParallelOptions { MaxDegreeOfParallelism = 3 }, async (link, _) => results.Add(await probe.CheckAsync(link)));
    foreach (var group in results.GroupBy(r => r.Status)) Console.WriteLine($"{group.Key}: {group.Count()}");
    Console.WriteLine($"Checked={results.Count}, usable={results.Count(r => r.IsUsable)}");
    return loaded.Links.Count > 0 ? 0 : 1;
}

if (args.Contains("--external-scan-tests", StringComparer.OrdinalIgnoreCase))
{
    return await RunExternalScanTests();
}

if (args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
{
    return RunSmokeTests();
}

if (args.Contains("--validate-manifest", StringComparer.OrdinalIgnoreCase))
{
    var manifestPath = FindManifestPath();
    _ = TrustedBinaryManifest.Load(manifestPath);
    Console.WriteLine($"Trusted binary manifest valid: {manifestPath}");

    var macManifestPath = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(manifestPath)!)!, "macos", "trusted-binaries.json");
    if (File.Exists(macManifestPath))
    {
        _ = TrustedBinaryManifest.Load(macManifestPath);
        Console.WriteLine($"macOS trusted binary manifest valid: {macManifestPath}");
    }
    return 0;
}

if (args.Contains("--service-tests", StringComparer.OrdinalIgnoreCase))
{
    var statistics = new StatisticsService();
    var first = statistics.Update(100, 200);
    var second = statistics.Update(160, 275);
    if (first.UploadSpeed != 0 || first.DownloadSpeed != 0 || second.UploadSpeed != 60 || second.DownloadSpeed != 75)
        return Fail("StatisticsService returned incorrect deltas.");

    if (UpdateVersionHelper.IsNewerVersion("v1.4.15", "1.4.16"))
        return Fail("Older version v1.4.15 must not be considered newer than 1.4.16.");
    if (UpdateVersionHelper.IsNewerVersion("v1.4.16", "1.4.16"))
        return Fail("Same version v1.4.16 must not be considered newer than 1.4.16.");
    if (!UpdateVersionHelper.IsNewerVersion("v1.4.17", "1.4.16"))
        return Fail("v1.4.17 must be considered newer than 1.4.16.");
    if (!UpdateVersionHelper.IsNewerVersion("v1.5", "1.4.16"))
        return Fail("v1.5 must be considered newer than 1.4.16.");

    string mockReleasesJson = """
    [
      {
        "tag_name": "v1.4.16",
        "draft": false,
        "prerelease": false,
        "html_url": "https://github.com/pasnya/anarise-vpn/releases/tag/v1.4.16",
        "assets": [
          { "name": "AnariseVPN-macOS-arm64-v1.4.16.zip", "browser_download_url": "https://example.com/mac.zip" },
          { "name": "AnariseVPN-macOS-arm64-v1.4.16.dmg", "browser_download_url": "https://example.com/mac.dmg" },
          { "name": "AnariseVPN-Windows-x64-v1.4.16.zip", "browser_download_url": "https://example.com/win.zip" }
        ]
      },
      {
        "tag_name": "v1.4.15",
        "draft": false,
        "prerelease": false,
        "html_url": "https://github.com/pasnya/anarise-vpn/releases/tag/v1.4.15",
        "assets": [
          { "name": "AnariseVPN-Windows-x64-v1.4.15.zip", "browser_download_url": "https://example.com/win15.zip" }
        ]
      }
    ]
    """;

    if (UpdateVersionHelper.FindNewerRelease(mockReleasesJson, "1.4.16", "windows") != null)
        return Fail("Running 1.4.16 must not receive update prompt when releases are 1.4.16 and 1.4.15.");

    var from15Win = UpdateVersionHelper.FindNewerRelease(mockReleasesJson, "1.4.15", "windows");
    if (from15Win == null || from15Win.LatestVersion != "1.4.16" || from15Win.DownloadUrl != "https://example.com/win.zip")
        return Fail($"Windows update from 1.4.15 picked wrong release/asset: {from15Win?.LatestVersion} / {from15Win?.DownloadUrl}");

    var from15Mac = UpdateVersionHelper.FindNewerRelease(mockReleasesJson, "1.4.15", "macos");
    if (from15Mac == null || from15Mac.LatestVersion != "1.4.16" || from15Mac.DownloadUrl != "https://example.com/mac.dmg")
        return Fail($"macOS update from 1.4.15 picked wrong release/asset: {from15Mac?.LatestVersion} / {from15Mac?.DownloadUrl}");

    string plainSecret = "{\"socksPort\":20808,\"currentConfigLink\":\"vless://secret\"}";
    string protectedSecret = SecureStorage.Protect(plainSecret);
    if (protectedSecret == plainSecret)
        return Fail("SecureStorage.Protect must not return plaintext.");
    if (SecureStorage.Unprotect(protectedSecret) != plainSecret)
        return Fail("SecureStorage.Unprotect failed to round-trip protected secret.");

    if (!DesktopCoreServices.IsSupportedShareLink("vless://00000000-0000-4000-8000-000000000001@example.com:443") ||
        !DesktopCoreServices.IsSupportedShareLink("hy2://pass@example.com:443") ||
        DesktopCoreServices.IsSupportedShareLink("http://example.com"))
        return Fail("DesktopCoreServices.IsSupportedShareLink returned unexpected result.");

    string parsedHysteria = LinkParser.Parse("hy2://pass@198.51.100.10:8443?sni=example.com&obfs=salamander&obfs-password=secret", 20808, 20809);
    if (DesktopCoreServices.ExtractServerIp(parsedHysteria) != "198.51.100.10")
        return Fail("DesktopCoreServices.ExtractServerIp failed for Hysteria2 IP.");
    string runtimeHy2 = DesktopCoreServices.BuildRuntimeCoreConfig(parsedHysteria, 20808, 20809, isMieru: false, isHysteria: true);
    using var hy2Doc = JsonDocument.Parse(runtimeHy2);
    if (hy2Doc.RootElement.GetProperty("socks5").GetProperty("listen").GetString() != "127.0.0.1:20808" ||
        hy2Doc.RootElement.GetProperty("obfs").GetProperty("salamander").GetProperty("password").GetString() != "secret")
        return Fail("DesktopCoreServices.BuildRuntimeCoreConfig failed for Hysteria2.");

    Console.WriteLine("Service tests: StatisticsService, UpdateVersionHelper, SecureStorage, and DesktopCoreServices passed");
    return 0;
}

var link = Console.In.ReadToEnd().Trim();
if (string.IsNullOrEmpty(link))
{
    Console.Error.WriteLine("A share link is required on standard input.");
    return 2;
}

Console.Write(LinkParser.Parse(link));
return 0;

static async Task<int> RunProbeTests()
{
    void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine($"PASS {name}"); }
    Check(ServerProbe.IsExpectedResponse(ServerProbe.Targets[0], 204, "") && !ServerProbe.IsExpectedResponse(ServerProbe.Targets[0], 200, "<html>portal</html>") && !ServerProbe.IsExpectedResponse(ServerProbe.Targets[0], 302, ""), "strict-http-response");
    Check(ServerProbe.IsExpectedResponse(ServerProbe.Targets[2], 200, "success\n") && !ServerProbe.IsExpectedResponse(ServerProbe.Targets[2], 200, "wrong"), "expected-body");
    Check(ServerProbe.EvaluateSuccess(new[] { "a", "b" }, new long[] { 20, 40 }, false)?.Status == ProbeStatus.Verified, "two-targets");
    Check(ServerProbe.EvaluateSuccess(new[] { "a" }, new long[] { 20 }, true) == null && ServerProbe.EvaluateSuccess(new[] { "a" }, new long[] { 20, 40 }, false) == null && ServerProbe.EvaluateSuccess(new[] { "a" }, new long[] { 20, 40 }, true)?.Status == ProbeStatus.Limited, "stable-partial-only");
    const string vless = "vless://00000000-0000-4000-8000-000000000001@example.com:443?type=grpc&serviceName=service&security=tls";
    var config = ServerProbe.BuildCoreConfig(LinkParser.Parse(vless, 32000, 32001), 32000, 32002);
    Check(config["routing"]!["rules"]!.AsArray().Count == 1 && config["outbounds"]![0]!["streamSettings"]!["grpcSettings"]!["serviceName"]!.ToString() == "service", "grpc-and-no-direct-bypass");
    const string vlessHttpUpgrade = "vless://00000000-0000-4000-8000-000000000001@example.com:443?type=httpupgrade&path=%2Fws&host=cdn.example.com&security=tls";
    var httpUpgradeCfg = ServerProbe.BuildCoreConfig(LinkParser.Parse(vlessHttpUpgrade, 32000, 32001), 32000, 32002);
    Check(httpUpgradeCfg["outbounds"]![0]!["streamSettings"]!["httpupgradeSettings"]!["path"]!.ToString() == "/ws" &&
          httpUpgradeCfg["outbounds"]![0]!["streamSettings"]!["httpupgradeSettings"]!["host"]!.ToString() == "cdn.example.com", "httpupgrade-transport");
    var mieru = ServerProbe.BuildCoreConfig(LinkParser.Parse("mieru://user:password@example.com:443?transport=UDP"), 32000, 32002);
    Check(mieru["rpcPort"]!.GetValue<int>() == 32002 && mieru["profiles"]![0]!["servers"]![0]!["portBindings"]![0]!["protocol"]!.ToString() == "UDP", "mieru-own-core-and-udp");
    var hysteria = ServerProbe.BuildCoreConfig(LinkParser.Parse("hy2://password@example.com:443?sni=tls.example&obfs=salamander&obfs-password=secret"), 32000, 32002);
    Check(hysteria["tls"]!["sni"]!.ToString() == "tls.example" && hysteria["obfs"]!["salamander"]!["password"]!.ToString() == "secret", "hysteria-sni-obfs");
    var sourceText = vless + "#first\nvmess://synthetic\nss://unsupported";
    Check(ExternalSources.ParseSubscription(Convert.ToBase64String(Encoding.UTF8.GetBytes(sourceText)).TrimEnd('=')).Count == 2, "base64-supported-protocols");
    Check(ExternalSources.Interleave(new[] { new List<string> { vless + "#a", "hy2://one" }, new List<string> { vless + "#b", "vmess://two" } }).SequenceEqual(new[] { vless + "#a", "hy2://one", "vmess://two" }), "source-interleave-dedup");
    var probe = new ServerProbe(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()), Path.GetTempPath());
    Check((await probe.CheckAsync(vless)).Status == ProbeStatus.MissingEngine && !(new ServerProbeResult(ProbeStatus.EngineStartFailed)).IsUsable, "local-errors-not-dead-servers");
    Console.WriteLine("Probe/source tests: 11/11 passed");
    return 0;
}

static async Task<int> RunExternalScanTests()
{
    var cases = new[]
    {
        // Reachable proxy only after the first config batch.
        (Name: "next-config-batch", Count: 201, ReachableFrom: 200, WorkingIndex: 200, ExpectedPings: 201, ExpectedTraffic: 201),
        // Reachable ports in both batches, but credentials only work in batch two.
        (Name: "next-batch-after-proxy-failures", Count: 201, ReachableFrom: 0, WorkingIndex: 200, ExpectedPings: 201, ExpectedTraffic: 201),
        // The first 30 reachable ports fail the proxy check; later ones must run.
        (Name: "next-traffic-batch", Count: 35, ReachableFrom: 0, WorkingIndex: 34, ExpectedPings: 35, ExpectedTraffic: 35),
        (Name: "exhaust-list", Count: 405, ReachableFrom: 0, WorkingIndex: -1, ExpectedPings: 405, ExpectedTraffic: 405),
        (Name: "failed-tcp-still-verified", Count: 405, ReachableFrom: 405, WorkingIndex: 0, ExpectedPings: 200, ExpectedTraffic: 30),
        (Name: "stop-after-success", Count: 405, ReachableFrom: 0, WorkingIndex: 0, ExpectedPings: 200, ExpectedTraffic: 30)
    };
    foreach (var test in cases)
    {
        var links = Enumerable.Range(0, test.Count).Select(i => i.ToString()).ToList();
        var trafficLinks = new System.Collections.Concurrent.ConcurrentBag<string>();
        var result = await ExternalConfigScanner.ScanAsync(links,
            link => Task.FromResult(int.Parse(link) >= test.ReachableFrom ? (long)int.Parse(link) : -1L),
            link => { trafficLinks.Add(link); return Task.FromResult(int.Parse(link) == test.WorkingIndex
                ? new ServerProbeResult(ProbeStatus.Verified, 42, DateTimeOffset.UtcNow)
                : new ServerProbeResult(ProbeStatus.NetworkError)); });
        if (result.CheckedConfigs != test.ExpectedPings || result.TrafficChecks != test.ExpectedTraffic ||
            trafficLinks.Distinct().Count() != test.ExpectedTraffic ||
            result.WorkingConfigs.Count != (test.WorkingIndex >= 0 ? 1 : 0) ||
            (test.WorkingIndex >= 0 && result.WorkingConfigs[0].Link != test.WorkingIndex.ToString()))
            return Fail($"External scan failed: {test.Name}; pings={result.CheckedConfigs}, traffic={result.TrafficChecks}, uniqueTraffic={trafficLinks.Distinct().Count()}, working={string.Join(",", result.WorkingConfigs.Select(c => c.Link))}");
        Console.WriteLine($"PASS {test.Name}");
    }
    Console.WriteLine($"External scan tests: {cases.Length}/{cases.Length} passed");
    return 0;
}

static int RunSmokeTests()
{
    var vmessPayload = Convert.ToBase64String(Encoding.UTF8.GetBytes(
        "{\"v\":\"2\",\"ps\":\"smoke\",\"add\":\"example.com\",\"port\":443,\"id\":\"00000000-0000-4000-8000-000000000001\",\"aid\":0,\"net\":\"tcp\",\"type\":\"none\",\"tls\":\"\",\"sni\":\"example.com\"}"));

    var cases = new[]
    {
        new SmokeCase("vless", "vless://00000000-0000-4000-8000-000000000001@example.com:443?type=tcp&security=tls&sni=example.com#smoke", "vless", 443),
        new SmokeCase("vmess", $"vmess://{vmessPayload}", "vmess", 443),
        new SmokeCase("naive", "naive+https://user:password@example.com:443?type=tcp&security=tls&sni=example.com#smoke", "http", 443),
        new SmokeCase("hysteria2", "hysteria2://password@example.com:8443?sni=example.com&obfs=salamander&obfs-password=obfs-secret#smoke", "__hysteria2__", 8443),
        new SmokeCase("hy2-alias", "hy2://password@example.com:9443?sni=example.com", "__hysteria2__", 9443),
        new SmokeCase("mieru", "mieru://user:password@example.com:443?transport=TCP", "__mieru__", 443)
    };

    var failures = 0;
    foreach (var testCase in cases)
    {
        try
        {
            using var document = JsonDocument.Parse(LinkParser.Parse(testCase.Link, 20808, 20809));
            var root = document.RootElement;
            var protocol = root.TryGetProperty("_protocol", out var protocolProperty)
                ? protocolProperty.GetString() ?? ""
                : root.GetProperty("outbounds")[0].GetProperty("protocol").GetString() ?? "";
            var port = root.TryGetProperty("server_port", out var serverPort)
                ? serverPort.GetInt32()
                : GetOutboundPort(root.GetProperty("outbounds")[0]);

            if (!string.Equals(protocol, testCase.ExpectedProtocol, StringComparison.OrdinalIgnoreCase) || port != testCase.ExpectedPort)
            {
                Console.Error.WriteLine($"FAIL {testCase.Name}: protocol={protocol}, port={port}");
                failures++;
                continue;
            }

            Console.WriteLine($"PASS {testCase.Name}");
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"FAIL {testCase.Name}: {exception.Message}");
            failures++;
        }
    }

    Console.WriteLine($"Smoke tests: {cases.Length - failures}/{cases.Length} passed");
    return failures == 0 ? 0 : 1;
}

static string FindManifestPath()
{
    var directory = new DirectoryInfo(Environment.CurrentDirectory);
    while (directory != null)
    {
        var candidates = new[]
        {
            Path.Combine(directory.FullName, "windows", "trusted-binaries.json"),
            Path.Combine(directory.FullName, "anarise-vpn-src2", "windows", "trusted-binaries.json")
        };
        var candidate = candidates.FirstOrDefault(File.Exists);
        if (candidate != null)
            return candidate;
        directory = directory.Parent;
    }

    throw new FileNotFoundException("Could not locate windows/trusted-binaries.json from the current directory.");
}

static int GetOutboundPort(JsonElement outbound)
{
    var settings = outbound.GetProperty("settings");
    if (settings.TryGetProperty("vnext", out var vnext))
    {
        return vnext[0].GetProperty("port").GetInt32();
    }

    return settings.GetProperty("servers")[0].GetProperty("port").GetInt32();
}

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 1;
}

readonly record struct SmokeCase(string Name, string Link, string ExpectedProtocol, int ExpectedPort);
