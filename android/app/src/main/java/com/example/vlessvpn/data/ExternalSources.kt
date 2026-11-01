package com.example.vlessvpn.data

import android.content.Context
import org.json.JSONObject

data class ExternalSource(val name: String, val url: String) {
    val mirrorUrl: String get() {
        val parts = java.net.URI(url).path.trim('/').split('/')
        return "https://cdn.jsdelivr.net/gh/${parts[0]}/${parts[1]}@${parts[2]}/${parts.drop(3).joinToString("/")}"
    }
}

object ExternalSources {
    fun load(context: Context): List<ExternalSource> {
        val json = context.assets.open("external-sources.json").bufferedReader().use { JSONObject(it.readText()) }
        val sources = json.getJSONArray("sources")
        return (0 until sources.length()).map { index ->
            val item = sources.getJSONObject(index)
            val url = item.getString("url")
            val uri = java.net.URI(url)
            require(uri.scheme == "https" && uri.host == "raw.githubusercontent.com")
            ExternalSource(item.getString("name"), url)
        }
    }

    fun parse(content: String): List<String> {
        var decoded = content.trim().removePrefix("\uFEFF")
        if (!decoded.contains("://")) {
            decoded = try {
                val base64 = decoded.filterNot { it.isWhitespace() }.replace('-', '+').replace('_', '/')
                String(android.util.Base64.decode(base64.padEnd((base64.length + 3) / 4 * 4, '='), android.util.Base64.DEFAULT), Charsets.UTF_8)
            } catch (_: IllegalArgumentException) { return emptyList() }
        }
        val prefixes = listOf("vless://", "vmess://", "naive+https://", "hysteria2://", "hy2://", "mieru://", "mierus://")
        return decoded.lineSequence().map { it.trim() }.filter { link -> prefixes.any { link.startsWith(it, true) } }.toList()
    }

    fun interleave(sources: List<List<String>>): List<String> {
        val seen = mutableSetOf<String>()
        val result = mutableListOf<String>()
        for (index in 0 until (sources.maxOfOrNull { it.size } ?: 0))
            for (source in sources)
                if (index < source.size && seen.add(source[index].substringBefore('#'))) result.add(source[index])
        return result
    }
}
