package com.example.vlessvpn.data

import android.content.Context
import android.content.SharedPreferences
import android.util.Base64
import java.nio.charset.StandardCharsets
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** Keystore-backed encryption for values that contain VPN links or credentials. */
object SecurePreferences {
    private const val PREFS_NAME = "anarise_secure_prefs"
    private const val KEY_ALIAS = "anarise_vpn_aes_key"
    private const val KEY_PREFIX = "enc:"

    private fun preferences(context: Context): SharedPreferences =
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val existing = store.getKey(KEY_ALIAS, null) as? SecretKey
        if (existing != null) return existing
        val generator = KeyGenerator.getInstance("AES", "AndroidKeyStore")
        generator.init(256)
        return generator.generateKey()
    }

    private fun encrypt(value: String): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key())
        val encrypted = cipher.doFinal(value.toByteArray(StandardCharsets.UTF_8))
        return KEY_PREFIX + Base64.encodeToString(cipher.iv + encrypted, Base64.NO_WRAP)
    }

    private fun decrypt(value: String): String? {
        if (!value.startsWith(KEY_PREFIX)) return null
        return try {
            val payload = Base64.decode(value.removePrefix(KEY_PREFIX), Base64.DEFAULT)
            if (payload.size <= 12) return null
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, payload, 0, 12))
            String(cipher.doFinal(payload, 12, payload.size - 12), StandardCharsets.UTF_8)
        } catch (_: Exception) {
            null
        }
    }

    fun getString(context: Context, name: String): String? =
        preferences(context).getString(name, null)?.let(::decrypt)

    fun putString(context: Context, name: String, value: String) {
        preferences(context).edit().putString(name, encrypt(value)).apply()
    }

    fun remove(context: Context, name: String) {
        preferences(context).edit().remove(name).apply()
    }
}
