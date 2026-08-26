using System.Text;
using System.Text.Json;
using Anarise;

if (args.Contains("--smoke", StringComparer.OrdinalIgnoreCase))
{
    return RunSmokeTests();
}

if (args.Contains("--validate-manifest", StringComparer.OrdinalIgnoreCase))
{
    var manifestPath = FindManifestPath();
    _ = TrustedBinaryManifest.Load(manifestPath);
    Console.WriteLine($"Trusted binary manifest valid: {manifestPath}");
    return 0;
}

if (args.Contains("--service-tests", StringComparer.OrdinalIgnoreCase))
{
    var statistics = new StatisticsService();
    var first = statistics.Update(100, 200);
    var second = statistics.Update(160, 275);
    if (first.UploadSpeed != 0 || first.DownloadSpeed != 0 || second.UploadSpeed != 60 || second.DownloadSpeed != 75)
        return Fail("StatisticsService returned incorrect deltas.");
    Console.WriteLine("Service tests: StatisticsService passed");
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
