using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Anarise
{
    public static class LinkParser
    {
        public const string PROTOCOL_HYSTERIA2 = "__hysteria2__";

        public static bool IsHysteria2Config(string configJson)
        {
            try
            {
                using var doc = JsonDocument.Parse(configJson);
                if (doc.RootElement.TryGetProperty("_protocol", out var protoProp))
                {
                    return protoProp.GetString() == PROTOCOL_HYSTERIA2;
                }
            }
            catch { }
            return false;
        }

        public static string Parse(string link, int socksPort = 20808, int httpPort = 20809)
        {
            if (link.StartsWith("vless://"))
                return ParseVless(link, socksPort, httpPort);
            if (link.StartsWith("vmess://"))
                return ParseVmess(link, socksPort, httpPort);
            if (link.StartsWith("naive+https://"))
                return ParseNaive(link, socksPort, httpPort);
            if (link.StartsWith("hysteria2://") || link.StartsWith("hy2://"))
                return ParseHysteria2(link);

            throw new ArgumentException("Unsupported protocol: Only VLESS, VMess, NaiveProxy and Hysteria2 are supported");
        }

        private static string ParseHysteria2(string link)
        {
            // Normalize hy2:// to hysteria2://
            var normalizedLink = link;
            if (link.StartsWith("hy2://"))
            {
                normalizedLink = "hysteria2://" + link.Substring(6);
            }

            var uri = new Uri(normalizedLink);
            var queryParams = ParseQueryString(uri.Query);

            string auth = "";
            if (!string.IsNullOrEmpty(uri.UserInfo))
            {
                auth = uri.UserInfo;
            }
            else if (queryParams.TryGetValue("auth", out var qAuth))
            {
                auth = qAuth;
            }

            string host = uri.Host;
            int port = uri.Port != -1 ? uri.Port : 443;

            queryParams.TryGetValue("sni", out var sni);
            if (string.IsNullOrEmpty(sni)) sni = host;

            queryParams.TryGetValue("obfs-password", out var obfsPassword);
            if (string.IsNullOrEmpty(obfsPassword))
            {
                queryParams.TryGetValue("obfs_password", out obfsPassword);
            }

            queryParams.TryGetValue("obfs", out var obfsType);
            if (string.IsNullOrEmpty(obfsType) && !string.IsNullOrEmpty(obfsPassword))
            {
                obfsType = "salamander";
            }

            bool insecure = false;
            if (queryParams.TryGetValue("insecure", out var ins))
            {
                insecure = ins == "1" || ins.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            else if (queryParams.TryGetValue("allowInsecure", out var allowIns))
            {
                insecure = allowIns == "1" || allowIns.Equals("true", StringComparison.OrdinalIgnoreCase);
            }

            queryParams.TryGetValue("alpn", out var alpn);

            var configObj = new JsonObject
            {
                ["_protocol"] = PROTOCOL_HYSTERIA2,
                ["server"] = $"{host}:{port}",
                ["server_host"] = host,
                ["server_port"] = port,
                ["auth"] = auth,
                ["sni"] = sni,
                ["insecure"] = insecure
            };

            if (!string.IsNullOrEmpty(obfsType))
            {
                configObj["obfs_type"] = obfsType;
                configObj["obfs_password"] = obfsPassword ?? "";
            }

            if (!string.IsNullOrEmpty(alpn))
            {
                configObj["alpn"] = alpn;
            }

            return configObj.ToJsonString();
        }

        private static string ParseVless(string link, int socksPort, int httpPort)
        {
            var uri = new Uri(link);
            var queryParams = ParseQueryString(uri.Query);

            string host = uri.Host;
            int port = uri.Port != -1 ? uri.Port : 443;
            string uuid = uri.UserInfo;

            queryParams.TryGetValue("security", out var security);
            if (string.IsNullOrEmpty(security)) security = "none";

            queryParams.TryGetValue("sni", out var sni);
            queryParams.TryGetValue("type", out var network);
            if (string.IsNullOrEmpty(network)) network = "tcp";

            queryParams.TryGetValue("flow", out var flow);
            queryParams.TryGetValue("path", out var path);
            queryParams.TryGetValue("pbk", out var publicKey);
            queryParams.TryGetValue("sid", out var shortId);
            queryParams.TryGetValue("fp", out var fingerprint);
            if (string.IsNullOrEmpty(fingerprint)) fingerprint = "chrome";

            queryParams.TryGetValue("host", out var requestHost);
            queryParams.TryGetValue("mode", out var mode);
            if (string.IsNullOrEmpty(mode)) mode = "auto";

            queryParams.TryGetValue("extra", out var extra);

            return BuildConfigJson(
                protocol: "vless",
                host: host,
                port: port,
                uuid: uuid,
                security: security,
                sni: sni ?? "",
                network: network,
                flow: flow ?? "",
                path: path ?? "",
                publicKey: publicKey ?? "",
                shortId: shortId ?? "",
                fingerprint: fingerprint,
                requestHost: requestHost ?? "",
                mode: mode,
                extra: extra ?? "",
                allowInsecure: false,
                socksPort: socksPort,
                httpPort: httpPort
            );
        }

        private static string ParseVmess(string link, int socksPort, int httpPort)
        {
            string rawData = link.Substring("vmess://".Length);
            string jsonStr;
            try
            {
                // Pad base64 string if necessary
                rawData = rawData.Trim();
                int mod4 = rawData.Length % 4;
                if (mod4 > 0)
                {
                    rawData += new string('=', 4 - mod4);
                }
                byte[] bytes = Convert.FromBase64String(rawData);
                jsonStr = Encoding.UTF8.GetString(bytes);
            }
            catch (Exception ex)
            {
                throw new ArgumentException("Invalid VMess Base64 encoding", ex);
            }

            // Handle common malformed JSON
            jsonStr = jsonStr.Replace("\"tls\",", "\"tls\":\"tls\",");
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var v = JsonSerializer.Deserialize<Dictionary<string, object>>(jsonStr, options);

            string add = v.ContainsKey("add") ? v["add"]?.ToString() : "";
            int port = v.ContainsKey("port") ? Convert.ToInt32(v["port"]?.ToString()) : 443;
            string id = v.ContainsKey("id") ? v["id"]?.ToString() : "";
            string net = v.ContainsKey("net") ? v["net"]?.ToString() : "tcp";
            string path = v.ContainsKey("path") ? v["path"]?.ToString() : "";
            string tls = v.ContainsKey("tls") ? v["tls"]?.ToString() : "";
            string type = v.ContainsKey("type") ? v["type"]?.ToString() : "";
            string sni = v.ContainsKey("sni") ? v["sni"]?.ToString() : "";

            string security = (!string.IsNullOrEmpty(tls) && tls != "none") ? "tls" : "none";

            return BuildConfigJson(
                protocol: "vmess",
                host: add,
                port: port,
                uuid: id,
                security: security,
                sni: sni,
                network: net,
                flow: "",
                path: path,
                headerType: type,
                fingerprint: "chrome",
                socksPort: socksPort,
                httpPort: httpPort
            );
        }

        private static string ParseNaive(string link, int socksPort, int httpPort)
        {
            var uri = new Uri(link);
            var queryParams = ParseQueryString(uri.Query);

            string host = uri.Host;
            int port = uri.Port != -1 ? uri.Port : 443;
            string userInfo = uri.UserInfo;

            queryParams.TryGetValue("security", out var security);
            if (string.IsNullOrEmpty(security)) security = "tls";

            queryParams.TryGetValue("sni", out var sni);
            queryParams.TryGetValue("type", out var network);
            if (string.IsNullOrEmpty(network)) network = "tcp";

            bool allowInsecure = false;
            if (queryParams.TryGetValue("insecure", out var ins))
            {
                allowInsecure = ins == "1" || ins.Equals("true", StringComparison.OrdinalIgnoreCase);
            }
            else if (queryParams.TryGetValue("allowInsecure", out var allowIns))
            {
                allowInsecure = allowIns == "1" || allowIns.Equals("true", StringComparison.OrdinalIgnoreCase);
            }

            return BuildConfigJson(
                protocol: "http",
                host: host,
                port: port,
                uuid: userInfo,
                security: security,
                sni: sni ?? "",
                network: network,
                flow: "",
                allowInsecure: allowInsecure,
                socksPort: socksPort,
                httpPort: httpPort
            );
        }

        private static string BuildConfigJson(
            string protocol,
            string host,
            int port,
            string uuid,
            string security,
            string sni,
            string network,
            string flow,
            string path = "",
            string headerType = "",
            string publicKey = "",
            string shortId = "",
            string fingerprint = "chrome",
            string requestHost = "",
            string mode = "auto",
            string extra = "",
            bool allowInsecure = false,
            int socksPort = 20808,
            int httpPort = 20809)
        {
            var config = new JsonObject();
            config["log"] = new JsonObject { ["loglevel"] = "info" };

            config["dns"] = new JsonObject
            {
                ["servers"] = new JsonArray(
                    "https://1.1.1.1/dns-query",
                    "https://8.8.8.8/dns-query",
                    protocol == "http" ? "tcp://1.1.1.1" : "1.1.1.1",
                    protocol == "http" ? "tcp://8.8.8.8" : "8.8.8.8"
                ),
                ["queryStrategy"] = "UseIPv4"
            };

            var inbounds = new JsonArray();
            
            // SOCKS Inbound
            var socksInbound = new JsonObject
            {
                ["listen"] = "127.0.0.1",
                ["port"] = socksPort,
                ["protocol"] = "socks",
                ["settings"] = new JsonObject { ["udp"] = true },
                ["sniffing"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["destOverride"] = new JsonArray("http", "tls", "quic")
                }
            };
            inbounds.Add(socksInbound);

            // HTTP Inbound
            var httpInbound = new JsonObject
            {
                ["listen"] = "127.0.0.1",
                ["port"] = httpPort,
                ["protocol"] = "http",
                ["settings"] = new JsonObject { ["allowTransparent"] = false },
                ["sniffing"] = new JsonObject
                {
                    ["enabled"] = true,
                    ["destOverride"] = new JsonArray("http", "tls", "quic")
                }
            };
            inbounds.Add(httpInbound);

            config["inbounds"] = inbounds;

            // Outbounds
            var proxyOutbound = new JsonObject
            {
                ["tag"] = "proxy",
                ["protocol"] = protocol
            };

            if (protocol == "http")
            {
                var userPass = uuid.Split(':', 2);
                var user = userPass.Length > 0 ? userPass[0] : "";
                var pass = userPass.Length > 1 ? userPass[1] : "";

                var serverObj = new JsonObject
                {
                    ["address"] = host,
                    ["port"] = port
                };

                if (!string.IsNullOrEmpty(user))
                {
                    serverObj["users"] = new JsonArray(new JsonObject
                    {
                        ["user"] = user,
                        ["pass"] = pass
                    });
                }

                proxyOutbound["settings"] = new JsonObject
                {
                    ["servers"] = new JsonArray(serverObj)
                };
            }
            else
            {
                var userObj = new JsonObject
                {
                    ["id"] = uuid,
                    ["encryption"] = protocol == "vless" ? "none" : "auto"
                };
                if (!string.IsNullOrEmpty(flow))
                {
                    userObj["flow"] = flow;
                }

                var vnextObj = new JsonObject
                {
                    ["address"] = host,
                    ["port"] = port,
                    ["users"] = new JsonArray(userObj)
                };

                proxyOutbound["settings"] = new JsonObject
                {
                    ["vnext"] = new JsonArray(vnextObj)
                };
            }

            var streamSettings = new JsonObject
            {
                ["network"] = network,
                ["security"] = security,
                ["sockopt"] = new JsonObject { ["mark"] = 255 }
            };

            if (security == "tls")
            {
                var tlsSettings = new JsonObject
                {
                    ["serverName"] = !string.IsNullOrEmpty(sni) ? sni : host
                };
                if (allowInsecure)
                {
                    tlsSettings["allowInsecure"] = true;
                }
                if (protocol == "http")
                {
                    tlsSettings["alpn"] = new JsonArray("h2", "http/1.1");
                }
                streamSettings["tlsSettings"] = tlsSettings;
            }
            else if (security == "reality")
            {
                var realitySettings = new JsonObject
                {
                    ["serverName"] = !string.IsNullOrEmpty(sni) ? sni : host,
                    ["publicKey"] = publicKey,
                    ["shortId"] = shortId,
                    ["fingerprint"] = fingerprint
                };
                streamSettings["realitySettings"] = realitySettings;
            }

            if (network == "ws")
            {
                streamSettings["wsSettings"] = new JsonObject { ["path"] = path };
            }
            else if (network == "tcp" && headerType == "http")
            {
                streamSettings["tcpSettings"] = new JsonObject
                {
                    ["header"] = new JsonObject { ["type"] = "http" }
                };
            }
            else if (network == "xhttp")
            {
                var xhttpSettings = new JsonObject();
                if (!string.IsNullOrEmpty(path))
                {
                    xhttpSettings["path"] = path;
                }
                xhttpSettings["mode"] = !string.IsNullOrEmpty(mode) ? mode : "auto";
                
                string resolvedHost = !string.IsNullOrEmpty(requestHost) ? requestHost : 
                                      (!string.IsNullOrEmpty(sni) ? sni : host);
                xhttpSettings["host"] = resolvedHost;

                if (!string.IsNullOrEmpty(extra))
                {
                    try
                    {
                        xhttpSettings["extra"] = JsonNode.Parse(extra);
                    }
                    catch { }
                }
                streamSettings["xhttpSettings"] = xhttpSettings;
            }

            proxyOutbound["streamSettings"] = streamSettings;

            var dnsOutbound = new JsonObject { ["tag"] = "dns-out", ["protocol"] = "dns" };
            var directOutbound = new JsonObject 
            { 
                ["tag"] = "direct", 
                ["protocol"] = "freedom",
                ["settings"] = new JsonObject { ["domainStrategy"] = "UseIP" }
            };

            config["outbounds"] = new JsonArray(proxyOutbound, dnsOutbound, directOutbound);

            // Routing
            var rules = new JsonArray();
            rules.Add(new JsonObject { ["type"] = "field", ["port"] = "53", ["outboundTag"] = "dns-out" });
            rules.Add(new JsonObject { ["type"] = "field", ["ip"] = new JsonArray("geoip:private"), ["outboundTag"] = "direct" });

            if (protocol == "http")
            {
                rules.Add(new JsonObject { ["type"] = "field", ["network"] = "udp", ["outboundTag"] = "direct" });
                rules.Add(new JsonObject { ["type"] = "field", ["network"] = "tcp", ["outboundTag"] = "proxy" });
            }
            else
            {
                rules.Add(new JsonObject { ["type"] = "field", ["network"] = "tcp,udp", ["outboundTag"] = "proxy" });
            }

            config["routing"] = new JsonObject
            {
                ["domainStrategy"] = "IPIfNonMatch",
                ["rules"] = rules
            };

            return config.ToJsonString();
        }

        public static Dictionary<string, string> ParseQueryString(string query)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(query)) return dict;

            if (query.StartsWith("?")) query = query.Substring(1);

            var pairs = query.Split('&');
            foreach (var pair in pairs)
            {
                var parts = pair.Split('=', 2);
                if (parts.Length > 0)
                {
                    string key = parts[0];
                    string val = parts.Length > 1 ? Uri.UnescapeDataString(parts[1]) : "";
                    dict[key] = val;
                }
            }
            return dict;
        }
    }
}
