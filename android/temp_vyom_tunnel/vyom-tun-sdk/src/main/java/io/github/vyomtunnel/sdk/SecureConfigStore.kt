package io.github.vyomtunnel.sdk

import android.content.Context
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

internal object SecureConfigStore {
    private const val TAG = "SecureConfigStore"
    private const val PREFS_NAME = "vyom_secure_config"
    private const val KEY_ALIAS = "vyom_vpn_config_key"
    private const val ANDROID_KEY_STORE = "AndroidKeyStore"
    private const val KEY_PREFIX = "enc:"

    private fun keyStore(): KeyStore =
        KeyStore.getInstance(ANDROID_KEY_STORE).apply { load(null) }

    /**
     * Returns the AES/GCM key, creating it on first use.
     *
     * A KeyGenerator from the AndroidKeyStore provider MUST be initialized with a
     * [KeyGenParameterSpec]; the plain `init(keySize)` overload throws
     * UnsupportedOperationException and used to crash the app when a config was stored.
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
        val payload = cipher.iv + cipher.doFinal(value.toByteArray(StandardCharsets.UTF_8))
        return KEY_PREFIX + Base64.encodeToString(payload, Base64.NO_WRAP)
    }

    /** Stores [value] encrypted; never throws, so a keystore failure cannot crash a connect. */
    fun put(context: Context, value: String) {
        val encoded = try {
            encrypt(value, key())
        } catch (first: Exception) {
            Log.w(TAG, "Encryption failed, regenerating the keystore key", first)
            try {
                runCatching { keyStore().deleteEntry(KEY_ALIAS) }
                encrypt(value, generateKey())
            } catch (second: Exception) {
                Log.e(TAG, "Keystore unavailable, storing config unencrypted", second)
                value
            }
        }
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).edit()
            .putString("last_config", encoded).apply()
    }

    fun get(context: Context): String? {
        val stored = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .getString("last_config", null) ?: return null
        if (stored.startsWith(KEY_PREFIX)) return decrypt(stored.removePrefix(KEY_PREFIX))
        // Older builds stored bare Base64 ciphertext; a value written while the
        // keystore was unusable is plain text. Try the former, fall back to the latter.
        return decrypt(stored) ?: stored.takeIf { it.isNotBlank() }
    }

    private fun decrypt(encoded: String): String? = try {
        val payload = Base64.decode(encoded, Base64.DEFAULT)
        if (payload.size <= 12) {
            null
        } else {
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, payload, 0, 12))
            String(cipher.doFinal(payload, 12, payload.size - 12), StandardCharsets.UTF_8)
        }
    } catch (e: Exception) {
        Log.w(TAG, "Failed to decrypt the stored config", e)
        null
    }
}
