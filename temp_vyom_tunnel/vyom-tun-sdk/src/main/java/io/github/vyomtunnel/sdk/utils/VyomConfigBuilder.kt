package io.github.vyomtunnel.sdk.utils

import org.json.JSONObject

class VyomConfigBuilder {
    private var host: String = ""
    private var port: Int = 443
    private var uuid: String = ""
    private var protocol: String = "vless" // Default
    private var security: String = "reality"
    private var sni: String = "www.microsoft.com"
    private var publicKey: String = ""
    private var shortId: String = ""

    // Hysteria2 fields
    private var auth: String = ""
    private var obfsType: String = ""
    private var obfsPassword: String = ""
    private var insecure: Boolean = false

    fun vless() = apply { this.protocol = "vless" }
    fun vmess() = apply { this.protocol = "vmess" }
    fun hysteria2() = apply { this.protocol = "hysteria2" }

    fun server(host: String, port: Int) = apply {
        this.host = host
        this.port = port
    }

    fun credentials(uuid: String) = apply { this.uuid = uuid }

    /** Set Hysteria2 authentication password */
    fun auth(password: String) = apply { this.auth = password }

    /** Configure Salamander obfuscation for Hysteria2 */
    fun salamander(password: String) = apply {
        this.obfsType = "salamander"
        this.obfsPassword = password
    }

    /** Allow insecure TLS (skip certificate verification) */
    fun insecure(value: Boolean = true) = apply { this.insecure = value }

    fun reality(publicKey: String, shortId: String, sni: String = "www.microsoft.com") = apply {
        this.security = "reality"
        this.publicKey = publicKey
        this.shortId = shortId
        this.sni = sni
    }

    fun build(): String {
        if (protocol == "hysteria2") {
            return buildHysteria2Config()
        }
        return LinkParser.buildConfigJson(
            protocol = protocol,
            host = host,
            port = port,
            uuid = uuid,
            security = security,
            sni = sni,
            network = "tcp",
            flow = if (protocol == "vless") "xtls-rprx-vision" else "",
            publicKey = publicKey,
            shortId = shortId
        )
    }

    private fun buildHysteria2Config(): String {
        return JSONObject().apply {
            put("_protocol", LinkParser.PROTOCOL_HYSTERIA2)
            put("server", "$host:$port")
            put("server_host", host)
            put("server_port", port)
            put("auth", auth)
            put("sni", if (sni.isNotEmpty() && sni != "www.microsoft.com") sni else host)
            put("insecure", insecure)
            if (obfsType.isNotEmpty()) {
                put("obfs_type", obfsType)
                put("obfs_password", obfsPassword)
            }
        }.toString()
    }
}