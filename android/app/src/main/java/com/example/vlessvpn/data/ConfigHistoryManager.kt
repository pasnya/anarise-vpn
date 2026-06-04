package com.example.vlessvpn.data

import android.content.Context
import android.net.Uri
import org.json.JSONArray

object ConfigHistoryManager {
    private const val PREFS_NAME = "vless_history_prefs"
    private const val KEY_HISTORY = "config_history"

    /**
     * Extract a canonical identity (protocol:host:port) from a config link
     * for duplicate detection purposes.
     */
    fun extractConfigIdentity(link: String): String? {
        try {
            val trimmed = link.trim()
            val normalizedLink = if (trimmed.startsWith("hy2://")) {
                "hysteria2://" + trimmed.substring(6)
            } else {
                trimmed
            }

            val protocol = when {
                normalizedLink.startsWith("vless://") -> "vless"
                normalizedLink.startsWith("naive+https://") -> "naive"
                normalizedLink.startsWith("hysteria2://") -> "hysteria2"
                else -> return null
            }

            val authority = normalizedLink
                .substringAfter("://")
                .substringBefore("?")
                .substringBefore("#")

            val hostPort = if (authority.contains("@")) {
                authority.substringAfterLast("@")
            } else {
                authority
            }

            if (hostPort.isBlank()) return null

            val host = if (hostPort.startsWith("[")) {
                hostPort.substringBeforeLast("]").removePrefix("[")
            } else if (hostPort.contains(":")) {
                hostPort.substringBeforeLast(":")
            } else {
                hostPort
            }

            val port = if (hostPort.contains(":") && !hostPort.endsWith("]")) {
                hostPort.substringAfterLast(":").toIntOrNull() ?: 443
            } else {
                443
            }

            if (host.isNotBlank()) {
                return "$protocol:${host.lowercase()}:$port"
            }
        } catch (e: Exception) { }
        return null
    }

    /**
     * Check if a link with the same host:port+protocol identity already exists in history.
     * Returns the existing link if found, null otherwise.
     */
    fun findDuplicate(context: Context, link: String): String? {
        val newIdentity = extractConfigIdentity(link) ?: return null
        val history = getHistory(context)
        for (existingLink in history) {
            val existingIdentity = extractConfigIdentity(existingLink)
            if (existingIdentity != null && existingIdentity == newIdentity) {
                return existingLink
            }
        }
        return null
    }

    /**
     * Save a config link to history. Detects duplicates by host:port+protocol identity.
     * If a duplicate identity is found, the old entry is removed and the new link is added at top.
     * User configs have no size limit — they persist until manually deleted.
     */
    fun saveConfigToHistory(context: Context, link: String) {
        val trimmed = link.trim()
        if (trimmed.isBlank()) return
        val currentHistory = getHistory(context).toMutableList()

        // Check for duplicate by identity (host:port + protocol)
        val newIdentity = extractConfigIdentity(trimmed)
        if (newIdentity != null) {
            currentHistory.removeAll { existing ->
                extractConfigIdentity(existing) == newIdentity
            }
        } else {
            // Fallback to exact string comparison
            currentHistory.remove(trimmed)
        }

        currentHistory.add(0, trimmed) // Insert at top
        saveHistory(context, currentHistory)
    }

    fun getHistory(context: Context): List<String> {
        val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        val jsonStr = prefs.getString(KEY_HISTORY, null) ?: return emptyList()
        return try {
            val jsonArray = JSONArray(jsonStr)
            val list = mutableListOf<String>()
            for (i in 0 until jsonArray.length()) {
                list.add(jsonArray.getString(i))
            }
            list
        } catch (e: Exception) {
            emptyList()
        }
    }

    fun deleteFromHistory(context: Context, link: String) {
        val trimmed = link.trim()
        val currentHistory = getHistory(context).toMutableList()
        currentHistory.remove(trimmed)
        saveHistory(context, currentHistory)
    }

    private fun saveHistory(context: Context, history: List<String>) {
        val jsonArray = JSONArray()
        history.forEach { jsonArray.put(it) }
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .edit()
            .putString(KEY_HISTORY, jsonArray.toString())
            .apply()
    }
}
