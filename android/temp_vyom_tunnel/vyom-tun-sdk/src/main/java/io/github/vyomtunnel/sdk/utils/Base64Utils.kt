package io.github.vyomtunnel.sdk.utils

import android.util.Base64
import java.nio.charset.StandardCharsets

object Base64Utils {
    /**
     * Decodes a Base64 string safely, resolving padding issues and applying URL_SAFE fallback.
     */
    fun decode(input: String): String {
        var str = input.trim()
        while (str.length % 4 != 0) {
            str += "="
        }
        val bytes = try {
            Base64.decode(str, Base64.DEFAULT)
        } catch (e: Exception) {
            Base64.decode(str, Base64.URL_SAFE)
        }
        return String(bytes, StandardCharsets.UTF_8)
    }
}
