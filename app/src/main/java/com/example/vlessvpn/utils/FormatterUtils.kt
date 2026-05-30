package com.example.vlessvpn.utils

import android.net.Uri
import java.net.URLDecoder

object FormatterUtils {
    /**
     * Formats network speed into a human-readable string.
     */
    fun formatSpeed(bytesPerSecond: Long): String {
        if (bytesPerSecond < 1024) return "$bytesPerSecond B/s"
        val kb = bytesPerSecond / 1024.0
        if (kb < 1024) return String.format("%.1f KB/s", kb)
        val mb = kb / 1024.0
        return String.format("%.1f MB/s", mb)
    }

    /**
     * Formats duration in seconds to HH:MM:SS or MM:SS format.
     */
    fun formatDuration(seconds: Long): String {
        val h = seconds / 3600
        val m = (seconds % 3600) / 60
        val s = seconds % 60
        return if (h > 0) {
            String.format("%02d:%02d:%02d", h, m, s)
        } else {
            String.format("%02d:%02d", m, s)
        }
    }

    /**
     * Formats data usage in bytes into B, KB, MB, or GB.
     */
    fun formatBytes(bytes: Long): String {
        if (bytes < 1024) return "$bytes B"
        val kb = bytes / 1024.0
        if (kb < 1024) return String.format("%.1f KB", kb)
        val mb = kb / 1024.0
        if (mb < 1024) return String.format("%.1f MB", mb)
        val gb = mb / 1024.0
        return String.format("%.1f GB", gb)
    }

    /**
     * Extracts and decodes server name fragment or host from proxy URL.
     */
    fun getDisplayLabel(link: String): String {
        try {
            val hashIndex = link.lastIndexOf('#')
            if (hashIndex != -1 && hashIndex < link.length - 1) {
                val rawFragment = link.substring(hashIndex + 1)
                return URLDecoder.decode(rawFragment, "UTF-8")
            }
            val uri = Uri.parse(link)
            val host = uri.host
            if (!host.isNullOrBlank()) {
                return host
            }
        } catch (e: Exception) {}
        if (link.length > 30) {
            return link.take(30) + "..."
        }
        return link
    }
}
