// Standalone test script for LinkParser NaiveProxy parsing
import org.json.JSONArray
import org.json.JSONObject

// Mock android.net.Uri using java.net.URI for non-Android environment testing
class Uri(val host: String?, val port: Int, val userInfo: String?, val queryParams: Map<String, String>) {
    val queryParameterNames get() = queryParams.keys
    fun getQueryParameter(key: String) = queryParams[key]
    
    companion object {
        fun parse(s: String): Uri {
            val u = java.net.URI(s)
            val qMap = mutableMapOf<String, String>()
            u.query?.split("&")?.forEach {
                val parts = it.split("=")
                if (parts.size == 2) qMap[parts[0]] = java.net.URLDecoder.decode(parts[1], "UTF-8")
            }
            return Uri(u.host, u.port, u.userInfo, qMap)
        }
    }
}

fun buildConfigJson(
    protocol: String,
    host: String,
    port: Int,
    uuid: String,
    security: String,
    sni: String,
    network: String,
    flow: String,
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

    config.put("dns", JSONObject().apply {
        put("servers", JSONArray().apply {
            put("1.1.1.1")
            put("8.8.8.8")
        })
        put("queryStrategy", "UseIPv4")
    })

    config.put("inbounds", JSONArray().put(
        JSONObject().apply {
            put("listen", "127.0.0.1")
            put("port", 20808)
            put("protocol", "socks")
            put("settings", JSONObject().put("udp", true))
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
                            put("encryption", if (protocol == "vless") "none" else "auto")
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
                })
            }
        })
    }

    config.put("outbounds", JSONArray().apply {
        put(proxyOutbound)
        put(JSONObject().apply {
            put("tag", "direct")
            put("protocol", "freedom")
        })
    })

    return config.toString(2)
}

fun parseNaive(link: String): String {
    val uri = Uri.parse(link)
    
    val host = uri.host.orEmpty()
    val port = if (uri.port != -1) uri.port else 443
    val userInfo = uri.userInfo.orEmpty()

    val security = uri.getQueryParameter("security") ?: "tls"
    val sni = uri.getQueryParameter("sni").orEmpty()
    val network = uri.getQueryParameter("type") ?: "h2"
    val allowInsecure = uri.getQueryParameter("allowInsecure") == "1" || uri.getQueryParameter("insecure") == "1"

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

fun main() {
    println("--- Testing Link 1 (Simple Naive Proxy) ---")
    val link1 = "naive+https://u1_ejzOAv:rb88qXj7aVhmugUMlpgAd2VK@den.qsok.ru:443"
    val json1 = parseNaive(link1)
    println(json1)

    println("\n--- Testing Link 2 (Naive Proxy with custom query parameters) ---")
    val link2 = "naive+https://u1_5kJuEF:ZB7pi1mbkhPggT7sQzHbyrnh@serv.izarjewellery.ru:443?security=tls&insecure=0&allowInsecure=1&type=tcp&headerType=none"
    val json2 = parseNaive(link2)
    println(json2)
}

main()
