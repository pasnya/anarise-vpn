package io.github.vyomtunnel.sdk

import android.content.Context
import android.util.Base64
import java.nio.charset.StandardCharsets
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

internal object SecureConfigStore {
    private const val PREFS_NAME = "vyom_secure_config"
    private const val KEY_ALIAS = "vyom_vpn_config_key"

    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val existing = store.getKey(KEY_ALIAS, null) as? SecretKey
        if (existing != null) return existing
        return KeyGenerator.getInstance("AES", "AndroidKeyStore").apply { init(256) }.generateKey()
    }

    fun put(context: Context, value: String) {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val payload = cipher.iv + cipher.doFinal(value.toByteArray(StandardCharsets.UTF_8))
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).edit()
            .putString("last_config", Base64.encodeToString(payload, Base64.NO_WRAP)).apply()
    }

    fun get(context: Context): String? {
        val stored = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .getString("last_config", null) ?: return null
        return try {
            val payload = Base64.decode(stored, Base64.DEFAULT)
            if (payload.size <= 12) return null
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, payload, 0, 12))
            String(cipher.doFinal(payload, 12, payload.size - 12), StandardCharsets.UTF_8)
        } catch (_: Exception) {
            null
        }
    }
}
