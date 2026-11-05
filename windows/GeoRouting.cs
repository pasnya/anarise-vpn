using System.Text.RegularExpressions;
using System.Text.Json.Nodes;

namespace Anarise;

public sealed record GeoRoutingRule(string Matcher, string OutboundTag);

public static class GeoRouting
{
    private static readonly Regex MatcherPattern = new(@"^(geoip|geosite):[a-zA-Z0-9_-]{1,64}$", RegexOptions.Compiled);
    private static readonly HashSet<string> AllowedActions = new(StringComparer.OrdinalIgnoreCase) { "proxy", "direct", "block" };

    public static IReadOnlyList<GeoRoutingRule> Parse(string? value)
    {
        var rules = new List<GeoRoutingRule>();
        if (string.IsNullOrWhiteSpace(value)) return rules;

        foreach (string rawLine in value.Replace("\r", "").Split('\n'))
        {
            string line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            string[] parts = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (parts.Length != 2 || !MatcherPattern.IsMatch(parts[0]))
                throw new FormatException($"Некорректное geo-правило: '{line}'. Формат: geosite:category-ru=direct");
            if (!AllowedActions.Contains(parts[1]))
                throw new FormatException($"Неизвестное действие '{parts[1]}'. Используйте proxy, direct или block.");
            if (rules.Any(rule => string.Equals(rule.Matcher, parts[0], StringComparison.OrdinalIgnoreCase)))
                throw new FormatException($"Правило '{parts[0]}' указано повторно.");
            if (rules.Count >= 32)
                throw new FormatException("Можно задать не более 32 geo-правил.");
            rules.Add(new GeoRoutingRule(parts[0].ToLowerInvariant(), parts[1].ToLowerInvariant()));
        }

        return rules;
    }

    public static string ApplyToXrayConfig(string configJson, string? rulesText, string? outboundInterface = null)
    {
        var rules = Parse(rulesText);
        if (rules.Count == 0) return configJson;

        var config = JsonNode.Parse(configJson)?.AsObject() ?? throw new FormatException("Некорректная конфигурация Xray.");
        var routing = config["routing"] as JsonObject;
        if (routing == null)
        {
            routing = new JsonObject();
            config["routing"] = routing;
        }
        var xrayRules = routing["rules"] as JsonArray;
        if (xrayRules == null)
        {
            xrayRules = new JsonArray();
            routing["rules"] = xrayRules;
        }

        if (rules.Any(rule => rule.Matcher.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase)))
            routing["domainStrategy"] = "IPOnDemand";

        int insertionIndex = 0;
        while (insertionIndex < xrayRules.Count &&
               (xrayRules[insertionIndex]?["port"]?.ToString() == "53" ||
                (xrayRules[insertionIndex]?["network"]?.ToString() == "udp" && xrayRules[insertionIndex]?["port"]?.ToString() == "443")))
            insertionIndex++;

        foreach (var rule in rules)
            xrayRules.Insert(insertionIndex++, CreateXrayRule(rule));

        if (!string.IsNullOrWhiteSpace(outboundInterface) && config["outbounds"] is JsonArray outbounds)
        {
            var direct = outbounds.OfType<JsonObject>().FirstOrDefault(outbound => outbound["tag"]?.ToString() == "direct");
            if (direct != null)
            {
                var streamSettings = direct["streamSettings"] as JsonObject;
                if (streamSettings == null)
                {
                    streamSettings = new JsonObject();
                    direct["streamSettings"] = streamSettings;
                }
                streamSettings["sockopt"] = new JsonObject { ["interface"] = outboundInterface };
            }
        }

        return config.ToJsonString();
    }

    public static string BuildRouterConfig(int socksPort, int httpPort, int upstreamSocksPort,
        string? proxyUsername, string? proxyPassword, string? rulesText, string? outboundInterface = null)
    {
        var inbounds = new JsonArray
        {
            new JsonObject
            {
                ["listen"] = "127.0.0.1", ["port"] = socksPort, ["protocol"] = "socks",
                ["settings"] = BuildInboundAuthSettings("socks", proxyUsername, proxyPassword),
                ["sniffing"] = new JsonObject { ["enabled"] = true, ["destOverride"] = new JsonArray("http", "tls", "quic"), ["routeOnly"] = true }
            },
            new JsonObject
            {
                ["listen"] = "127.0.0.1", ["port"] = httpPort, ["protocol"] = "http",
                ["settings"] = BuildInboundAuthSettings("http", proxyUsername, proxyPassword),
                ["sniffing"] = new JsonObject { ["enabled"] = true, ["destOverride"] = new JsonArray("http", "tls", "quic"), ["routeOnly"] = true }
            }
        };

        var routingRules = new JsonArray
        {
            new JsonObject { ["type"] = "field", ["network"] = "udp", ["port"] = "443", ["outboundTag"] = "block" },
            new JsonObject { ["type"] = "field", ["inboundTag"] = new JsonArray("geo-dns"), ["outboundTag"] = "proxy" },
            new JsonObject { ["type"] = "field", ["port"] = "53", ["outboundTag"] = "dns-out" }
        };
        var parsedRules = Parse(rulesText);
        foreach (var rule in parsedRules) routingRules.Add(CreateXrayRule(rule));
        routingRules.Add(new JsonObject { ["type"] = "field", ["ip"] = new JsonArray("geoip:private"), ["outboundTag"] = "direct" });
        routingRules.Add(new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["outboundTag"] = "proxy" });

        var directOutbound = new JsonObject
        {
            ["tag"] = "direct", ["protocol"] = "freedom",
            ["settings"] = new JsonObject { ["domainStrategy"] = "AsIs" }
        };
        if (!string.IsNullOrWhiteSpace(outboundInterface))
        {
            directOutbound["streamSettings"] = new JsonObject
            {
                ["sockopt"] = new JsonObject { ["interface"] = outboundInterface }
            };
        }

        var config = new JsonObject
        {
            ["log"] = new JsonObject { ["loglevel"] = "warning" },
            ["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(
                    new JsonObject { ["address"] = "https://1.1.1.1/dns-query", ["tag"] = "geo-dns", ["queryStrategy"] = "UseIPv4" },
                    new JsonObject { ["address"] = "https://8.8.8.8/dns-query", ["tag"] = "geo-dns", ["queryStrategy"] = "UseIPv4" }),
                ["queryStrategy"] = "UseIPv4"
            },
            ["inbounds"] = inbounds,
            ["outbounds"] = new JsonArray
            {
                new JsonObject
                {
                    ["tag"] = "proxy", ["protocol"] = "socks",
                    ["settings"] = new JsonObject { ["servers"] = new JsonArray(new JsonObject { ["address"] = "127.0.0.1", ["port"] = upstreamSocksPort }) }
                },
                new JsonObject { ["tag"] = "dns-out", ["protocol"] = "dns" },
                directOutbound,
                new JsonObject { ["tag"] = "block", ["protocol"] = "blackhole" }
            },
            ["routing"] = new JsonObject
            {
                ["domainStrategy"] = parsedRules.Any(rule => rule.Matcher.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase)) ? "IPOnDemand" : "AsIs",
                ["rules"] = routingRules
            }
        };
        return config.ToJsonString();
    }

    private static JsonObject BuildInboundAuthSettings(string protocol, string? username, string? password)
    {
        var settings = new JsonObject();
        if (protocol == "socks") settings["udp"] = true;
        if (string.IsNullOrWhiteSpace(username) || string.IsNullOrEmpty(password)) return settings;
        if (protocol == "socks") settings["auth"] = "password";
        string property = protocol == "socks" ? "accounts" : "users";
        settings[property] = new JsonArray(new JsonObject { ["user"] = username, ["pass"] = password });
        return settings;
    }

    private static JsonObject CreateXrayRule(GeoRoutingRule rule)
    {
        string matcherType = rule.Matcher.StartsWith("geoip:", StringComparison.OrdinalIgnoreCase) ? "ip" : "domain";
        return new JsonObject
        {
            ["type"] = "field",
            [matcherType] = new JsonArray(rule.Matcher),
            ["network"] = "tcp,udp",
            ["outboundTag"] = rule.OutboundTag
        };
    }
}
