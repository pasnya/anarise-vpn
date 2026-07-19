package io.github.vyomtunnel.sdk.utils

import android.net.Uri
import android.util.Base64
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.nio.charset.StandardCharsets

object LinkParser {

    private const val TAG = "VyomLinkParser"
    private const val DEFAULT_PORT = 20808
    private const val DEFAULT_DNS = "8.8.8.8"

    /** Marker used in the generated JSON to identify Hysteria2 configs */
    const val PROTOCOL_HYSTERIA2 = "__hysteria2__"
    const val PROTOCOL_MIERU = "__mieru__"

    fun parse(link: String): String {
        val trimmed = link.trim()
        return try {
            when {
                trimmed.startsWith("vless://") -> parseVless(trimmed)
                trimmed.startsWith("vmess://") -> parseVmess(trimmed)
                trimmed.startsWith("naive+https://") -> parseNaive(trimmed)
                trimmed.startsWith("hysteria2://") || trimmed.startsWith("hy2://") -> parseHysteria2(trimmed)
                trimmed.startsWith("mieru://") || trimmed.startsWith("mierus://") -> parseMieru(trimmed)
                else -> throw IllegalArgumentException("Unsupported protocol: Only VLESS, VMess, NaiveProxy, Hysteria2 and Mieru are supported")
            }
        } catch (e: Exception) {
            Log.e(TAG, "Failed to parse link: $link", e)
            throw e
        }
    }

    /**
     * Checks whether the given config JSON was generated from a Hysteria2 link.
     */
    fun isHysteria2Config(configJson: String): Boolean {
        return try {
            val obj = JSONObject(configJson)
            obj.optString("_protocol") == PROTOCOL_HYSTERIA2
        } catch (e: Exception) {
            false
        }
    }

    /**
     * Checks whether the given config JSON was generated from a Mieru link.
     */
    fun isMieruConfig(configJson: String): Boolean {
        return try {
            val obj = JSONObject(configJson)
            obj.optString("_protocol") == PROTOCOL_MIERU
        } catch (e: Exception) {
            false
        }
    }

    private fun parseMieru(link: String): String {
        val prefix = if (link.startsWith("mierus://")) "mierus://" else "mieru://"
        val content = link.substring(prefix.length)

        try {
            val decodedBytes = Base64.decode(content.replace('-', '+').replace('_', '/'), Base64.DEFAULT)
            val decoded = String(decodedBytes, StandardCharsets.UTF_8)
            val json = JSONObject(decoded)
            val profiles = json.optJSONArray("profiles")
            if (profiles != null && profiles.length() > 0) {
                val profile = profiles.getJSONObject(0)
                val userObj = profile.getJSONObject("user")
                val username = userObj.getString("name")
                val password = userObj.getString("password")
                val servers = profile.getJSONArray("servers")
                val serverObj = servers.getJSONObject(0)
                val ipAddress = serverObj.optString("ipAddress").ifEmpty { serverObj.optString("domainName") }
                val portBindings = serverObj.getJSONArray("portBindings")
                val portObj = portBindings.getJSONObject(0)
                val port = portObj.optInt("port", 443)
                val protocol = portObj.optString("protocol", "TCP")
                val multiplexing = profile.optJSONObject("multiplexing")?.optString("level", "MULTIPLEXING_LOW") ?: "MULTIPLEXING_LOW"

                return JSONObject().apply {
                    put("_protocol", PROTOCOL_MIERU)
                    put("server", "$ipAddress:$port")
                    put("server_host", ipAddress)
                    put("server_port", port)
                    put("username", username)
                    put("password", password)
                    put("multiplexing", multiplexing)
                    put("transport", protocol)
                }.toString()
            }
        } catch (e: Exception) {
            // Fallback to simple link format
        }

        try {
            val normalizedLink = link.replace("mierus://", "http://").replace("mieru://", "http://")
            val uri = Uri.parse(normalizedLink)
            val params = uri.queryParameterNames.associateWith { uri.getQueryParameter(it).orEmpty() }
            val host = uri.host.orEmpty()
            val port = if (uri.port != -1) uri.port else 443

            val userInfo = uri.userInfo.orEmpty()
            val userPass = userInfo.split(":", limit = 2)
            val username = userPass.getOrNull(0).orEmpty()
            val password = userPass.getOrNull(1).orEmpty()

            val multiplexing = params["multiplexing"]?.ifEmpty { "MULTIPLEXING_LOW" } ?: "MULTIPLEXING_LOW"
            val transport = params["transport"]?.ifEmpty { "TCP" } ?: "TCP"

            return JSONObject().apply {
                put("_protocol", PROTOCOL_MIERU)
                put("server", "$host:$port")
                put("server_host", host)
                put("server_port", port)
                put("username", username)
                put("password", password)
                put("multiplexing", multiplexing)
                put("transport", transport)
            }.toString()
        } catch (e: Exception) {
            throw IllegalArgumentException("Invalid Mieru link format", e)
        }
    }


    /**
     * Parses a hysteria2:// or hy2:// URI into a JSON config envelope.
     *
     * URI format: hysteria2://auth@host:port?sni=xxx&obfs=salamander&obfs-password=xxx&insecure=1#name
     *
     * The returned JSON is NOT an Xray config — it's a lightweight envelope
     * that VyomVpnService uses to launch HysteriaEngine.
     */
    private fun parseHysteria2(link: String): String {
        // Normalize hy2:// to hysteria2:// for standard URI parsing
        val normalizedLink = if (link.startsWith("hy2://")) {
            link.replaceFirst("hy2://", "hysteria2://")
        } else {
            link
        }

        val uri = Uri.parse(normalizedLink)
        val params = uri.queryParameterNames.associateWith { uri.getQueryParameter(it).orEmpty() }
        val auth = uri.userInfo.orEmpty().ifEmpty { params["auth"].orEmpty() }
        val host = uri.host.orEmpty()
        val port = if (uri.port != -1) uri.port else 443

        val sni = params["sni"].orEmpty()
        val obfsPassword = params["obfs-password"].orEmpty().ifEmpty { params["obfs_password"].orEmpty() }
        var obfsType = params["obfs"].orEmpty()
        if (obfsType.isEmpty() && obfsPassword.isNotEmpty()) {
            obfsType = "salamander"
        }
        val insecure = params["insecure"] == "1" || params["allowInsecure"] == "1" ||
                params["insecure"].equals("true", ignoreCase = true) ||
                params["allowInsecure"].equals("true", ignoreCase = true)
        val alpn = params["alpn"].orEmpty()

        val config = JSONObject().apply {
            put("_protocol", PROTOCOL_HYSTERIA2)
            put("server", "$host:$port")
            put("server_host", host)
            put("server_port", port)
            put("auth", auth)
            put("sni", if (sni.isNotEmpty()) sni else host)
            put("insecure", insecure)
            if (obfsType.isNotEmpty()) {
                put("obfs_type", obfsType)
                put("obfs_password", obfsPassword)
            }
            if (alpn.isNotEmpty()) {
                put("alpn", alpn)
            }
        }

        return config.toString()
    }

    private fun parseVless(link: String): String {
        val uri = Uri.parse(link)
        val params = uri.queryParameterNames.associateWith { uri.getQueryParameter(it).orEmpty() }

        return buildConfigJson(
            protocol = "vless",
            host = uri.host.orEmpty(),
            port = if (uri.port != -1) uri.port else 443,
            uuid = uri.userInfo.orEmpty(),
            security = params["security"] ?: "none",
            sni = params["sni"].orEmpty(),
            network = params["type"] ?: "tcp",
            flow = params["flow"].orEmpty(),
            encryption = params["encryption"].orEmpty().ifEmpty { "none" },
            path = params["path"].orEmpty(),
            publicKey = params["pbk"].orEmpty(),
            shortId = params["sid"].orEmpty(),
            fingerprint = params["fp"] ?: "chrome",
            requestHost = params["host"].orEmpty(),
            mode = params["mode"] ?: "auto",
            extra = params["extra"].orEmpty()
        )
    }

    private fun parseVmess(link: String): String {
        val rawData = link.removePrefix("vmess://")
        val jsonStr = try {
            Base64Utils.decode(rawData)
        } catch (e: Exception) {
            throw IllegalArgumentException("Invalid VMess Base64 encoding")
        }

        // Handle common malformed JSON from certain providers (e.g., missing values for keys)
        val sanitizedJson = jsonStr.replace("\"tls\",", "\"tls\":\"tls\",")
        val v = JSONObject(sanitizedJson)

        return buildConfigJson(
            protocol = "vmess",
            host = v.getString("add"),
            port = v.optInt("port", 443),
            uuid = v.getString("id"),
            security = if (v.optString("tls").isNotEmpty() && v.optString("tls") != "none") "tls" else "none",
            sni = v.optString("sni").orEmpty(),
            network = v.optString("net", "tcp"),
            path = v.optString("path").orEmpty(),
            headerType = v.optString("type").orEmpty(),
            flow = ""
        )
    }

    private fun parseNaive(link: String): String {
        val uri = Uri.parse(link)
        val params = uri.queryParameterNames.associateWith { uri.getQueryParameter(it).orEmpty() }

        val host = uri.host.orEmpty()
        val port = if (uri.port != -1) uri.port else 443
        val userInfo = uri.userInfo.orEmpty()

        val security = params["security"] ?: "tls"
        val sni = params["sni"].orEmpty()
        val network = params["type"] ?: "tcp"
        val allowInsecure = params["allowInsecure"] == "1" || params["insecure"] == "1"

        return buildConfigJson(
            protocol = "http",
            host = host,
            port = port,
            uuid = userInfo,
            security = security,
            sni = sni,
            network = network,
            flow = "",
            allowInsecure = allowInsecure
        )
    }

    internal fun buildConfigJson(
        protocol: String,
        host: String,
        port: Int,
        uuid: String,
        security: String,
        sni: String,
        network: String,
        flow: String,
        encryption: String = "",
        path: String = "",
        headerType: String = "",
        publicKey: String = "",
        shortId: String = "",
        fingerprint: String = "chrome",
        requestHost: String = "",
        mode: String = "auto",
        extra: String = "",
        allowInsecure: Boolean = false
    ): String {

        val config = JSONObject()

        config.put("log", JSONObject().put("loglevel", "info"))

//        config.put("fakedns", JSONArray().put(
//            JSONObject().apply {
//                put("ipPool", "198.18.0.0/15")
//                put("poolSize", 65535)
//            }
//        ))

        config.put("dns", JSONObject().apply {
            put("servers", JSONArray().apply {
                if (protocol == "http") {
                    put("tcp://1.1.1.1")
                    put("tcp://8.8.8.8")
                } else {
                    put("1.1.1.1")
                    put("8.8.8.8")
                }
            })
            put("queryStrategy", "UseIPv4")
        })

        config.put("inbounds", JSONArray().put(
            JSONObject().apply {
                put("listen", "127.0.0.1")
                put("port", 20808)
                put("protocol", "socks")
                put("settings", JSONObject().put("udp", true))
                put("sniffing", JSONObject().apply {
                    put("enabled", true)
                    put("destOverride", JSONArray().apply {
                        put("http"); put("tls"); put("quic")
                    })
                })
            }
        ))

        val proxyOutbound = JSONObject().apply {
            put("tag", "proxy")
            put("protocol", protocol)
            
            if (protocol == "http") {
                val userPass = uuid.split(":", limit = 2)
                val user = userPass.getOrNull(0).orEmpty()
                val pass = userPass.getOrNull(1).orEmpty()
                put("settings", JSONObject().apply {
                    put("servers", JSONArray().put(
                        JSONObject().apply {
                            put("address", host)
                            put("port", port)
                            if (user.isNotEmpty()) {
                                put("users", JSONArray().put(
                                    JSONObject().apply {
                                        put("user", user)
                                        put("pass", pass)
                                    }
                                ))
                            }
                        }
                    ))
                })
            } else {
                put("settings", JSONObject().put("vnext", JSONArray().put(
                    JSONObject().apply {
                        put("address", host)
                        put("port", port)
                        put("users", JSONArray().put(
                            JSONObject().apply {
                                put("id", uuid)
                                // Preserve modern VLESS ML-KEM / 0-RTT
                                // negotiation data from the share link.
                                put("encryption", if (protocol == "vless") encryption.ifEmpty { "none" } else "auto")
                                if (flow.isNotEmpty()) put("flow", flow)
                            }
                        ))
                    }
                )))
            }

            put("streamSettings", JSONObject().apply {
                put("network", network)
                put("security", security)
                put("sockopt", JSONObject().apply {
                    put("mark", 255)
                })

                when (security) {
                    "tls" -> put("tlsSettings", JSONObject().apply {
                        put("serverName", if (sni.isNotEmpty()) sni else host)
                        if (allowInsecure) {
                            put("allowInsecure", true)
                        }
                        if (protocol == "http") {
                            put("alpn", JSONArray().apply {
                                put("h2")
                                put("http/1.1")
                            })
                        }
                    })
                    "reality" -> put("realitySettings", JSONObject().apply {
                        put("serverName", if (sni.isNotEmpty()) sni else host)
                        put("publicKey", publicKey)
                        put("shortId", shortId)
                        put("fingerprint", fingerprint)
                    })
                }

                if (network == "ws") {
                    put("wsSettings", JSONObject().put("path", path))
                }
                if (network == "tcp" && headerType == "http") {
                    put("tcpSettings", JSONObject().put("header",
                        JSONObject().put("type", "http")
                    ))
                }
                if (network == "xhttp") {
                    put("xhttpSettings", JSONObject().apply {
                        if (path.isNotEmpty()) put("path", path)
                        put("mode", mode.ifEmpty { "auto" })
                        
                        val resolvedHost = requestHost.ifEmpty { sni }.ifEmpty { host }
                        put("host", resolvedHost)
                        
                        if (extra.isNotEmpty()) {
                            try {
                                put("extra", JSONObject(extra))
                            } catch (e: Exception) {}
                        }
                    })
                }
            })
        }

        val dnsOutbound = JSONObject().apply {
            put("tag", "dns-out")
            put("protocol", "dns")
        }

        val directOutbound = JSONObject().apply {
            put("tag", "direct")
            put("protocol", "freedom")
            put("settings", JSONObject().put("domainStrategy", "UseIP"))
        }

        config.put("outbounds", JSONArray().apply {
            put(proxyOutbound)
            put(dnsOutbound)
            put(directOutbound)
        })

        config.put("routing", JSONObject().apply {
            put("domainStrategy", "IPIfNonMatch")
            put("rules", JSONArray().apply {
                put(JSONObject().apply {
                    put("type", "field")
                    put("port", "53") // Intercept DNS
                    put("outboundTag", "dns-out")
                })
                put(JSONObject().apply {
                    put("type", "field")
                    put("ip", JSONArray().put("geoip:private"))
                    put("outboundTag", "direct")
                })
                if (protocol == "http") {
                    put(JSONObject().apply {
                        put("type", "field")
                        put("network", "udp")
                        put("outboundTag", "direct")
                    })
                    put(JSONObject().apply {
                        put("type", "field")
                        put("network", "tcp")
                        put("outboundTag", "proxy")
                    })
                } else {
                    put(JSONObject().apply {
                        put("type", "field")
                        put("network", "tcp,udp")
                        put("outboundTag", "proxy")
                    })
                }
            })
        })

        return config.toString()
    }


}
