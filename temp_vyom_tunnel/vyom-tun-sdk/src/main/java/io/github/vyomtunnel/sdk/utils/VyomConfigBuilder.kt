package io.github.vyomtunnel.sdk.utils

class VyomConfigBuilder {
    private var host: String = ""
    private var port: Int = 443
    private var uuid: String = ""
    private var protocol: String = "vless" // Default
    private var security: String = "reality"
    private var sni: String = "www.microsoft.com"
    private var publicKey: String = ""
    private var shortId: String = ""

    fun vless() = apply { this.protocol = "vless" }
    fun vmess() = apply { this.protocol = "vmess" }

    fun server(host: String, port: Int) = apply {
        this.host = host
        this.port = port
    }

    fun credentials(uuid: String) = apply { this.uuid = uuid }

    fun reality(publicKey: String, shortId: String, sni: String = "www.microsoft.com") = apply {
        this.security = "reality"
        this.publicKey = publicKey
        this.shortId = shortId
        this.sni = sni
    }

    fun build(): String {
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
}