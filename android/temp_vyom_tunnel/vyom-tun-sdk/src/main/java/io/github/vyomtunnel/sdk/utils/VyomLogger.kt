package io.github.vyomtunnel.sdk.utils

import android.content.Context
import android.content.Intent
import android.util.Log
import io.github.vyomtunnel.sdk.VyomVpnManager

object VyomLogger {
    private const val TAG = "VyomSDK"

    /**
     * Broadcasts a log message from the VPN process to the UI process.
     */
    fun i(context: Context, message: String) {
        Log.d(TAG, message)
        broadcast(context, message)
    }

    fun e(context: Context, message: String, throwable: Throwable? = null) {
        Log.e(TAG, message, throwable)
        broadcast(context, "ERROR: $message ${throwable?.message ?: ""}")
    }

    private fun broadcast(context: Context, message: String) {
        val intent = Intent(VyomVpnManager.ACTION_SDK_LOGS).apply {
            putExtra("MSG", message)
            setPackage(context.packageName)
        }
        context.sendBroadcast(intent)
    }
}