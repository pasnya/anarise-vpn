package com.example.vlessvpn.data

import android.content.Context
import android.content.SharedPreferences
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import android.util.Log
import java.nio.charset.StandardCharsets
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/** Keystore-backed encryption for values that contain VPN links or credentials. */
object SecurePreferences {
    private const val TAG = "SecurePreferences"
    private const val PREFS_NAME = "anarise_secure_prefs"
    private const val KEY_ALIAS = "anarise_vpn_aes_key"
    private const val KEY_PREFIX = "enc:"
    private const val ANDROID_KEY_STORE = "AndroidKeyStore"

    private fun preferences(context: Context): SharedPreferences =
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)

    private fun keyStore(): KeyStore =
        KeyStore.getInstance(ANDROID_KEY_STORE).apply { load(null) }

    /**
     * Returns the AES/GCM key, creating it on first use.
     *
     * A KeyGenerator from the AndroidKeyStore provider MUST be initialized with a
     * [KeyGenParameterSpec]; calling the plain `init(keySize)` overload throws
     * UnsupportedOperationException ("Cannot initialize without a
     * android.security.keystore.KeyGenParameterSpec parameter"), which used to crash
     * the app whenever a config was saved (paste from clipboard / QR scan).
     */
    private fun key(): SecretKey {
        (keyStore().getKey(KEY_ALIAS, null) as? SecretKey)?.let { return it }
        return generateKey()
    }

    private fun generateKey(): SecretKey {
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, ANDROID_KEY_STORE)
        generator.init(
            KeyGenParameterSpec.Builder(
                KEY_ALIAS,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return generator.generateKey()
    }

    private fun encrypt(value: String, secretKey: SecretKey): String {
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, secretKey)
        val encrypted = cipher.doFinal(value.toByteArray(StandardCharsets.UTF_8))
        return KEY_PREFIX + Base64.encodeToString(cipher.iv + encrypted, Base64.NO_WRAP)
    }

    private fun decrypt(value: String): String? {
        return try {
            val payload = Base64.decode(value.removePrefix(KEY_PREFIX), Base64.DEFAULT)
            if (payload.size <= 12) return null
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, payload, 0, 12))
            String(cipher.doFinal(payload, 12, payload.size - 12), StandardCharsets.UTF_8)
        } catch (e: Exception) {
            Log.w(TAG, "Failed to decrypt a stored value", e)
            null
        }
    }

    fun getString(context: Context, name: String): String? {
        val stored = preferences(context).getString(name, null) ?: return null
        // Values written by a build that could not reach the keystore are stored as
        // plain text; return them as-is so no history is lost.
        if (!stored.startsWith(KEY_PREFIX)) return stored
        return decrypt(stored)
    }

    /**
     * Stores [value] encrypted. Never throws: if the keystore is unusable on this
     * device the value is stored unencrypted rather than crashing the caller.
     */
    fun putString(context: Context, name: String, value: String) {
        val encoded = try {
            encrypt(value, key())
        } catch (first: Exception) {
            Log.w(TAG, "Encryption failed, regenerating the keystore key", first)
            try {
                // A key left over from an older build may lack GCM purposes; replace it.
                runCatching { keyStore().deleteEntry(KEY_ALIAS) }
                encrypt(value, generateKey())
            } catch (second: Exception) {
                Log.e(TAG, "Keystore unavailable, storing value unencrypted", second)
                value
            }
        }
        preferences(context).edit().putString(name, encoded).apply()
    }

    fun remove(context: Context, name: String) {
        preferences(context).edit().remove(name).apply()
    }
}
