package io.github.vyomtunnel.sdk

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.util.Log

class VyomBootReceiver : BroadcastReceiver() {

    companion object {
        private const val TAG = "VyomBootReceiver"

        // Some devices use different actions for boot completion
        private val BOOT_ACTIONS = setOf(
            Intent.ACTION_BOOT_COMPLETED,
            "android.intent.action.QUICKBOOT_POWERON",
            "com.htc.intent.action.QUICKBOOT_POWERON"
        )
    }

    override fun onReceive(context: Context, intent: Intent?) {
        val action = intent?.action ?: return
        if (!BOOT_ACTIONS.contains(action)) return

        Log.i(TAG, "Boot detected: $action")
        if (!VyomVpnManager.isAutoStartEnabled(context)) {
            Log.i(TAG, "Auto-start disabled by user")
            return
        }
        if (!VyomVpnManager.wasVpnRunning(context)) {
            Log.i(TAG, "VPN not running before shutdown")
            return
        }
        if (!VyomVpnManager.isPermissionGranted(context)) {
            Log.w(TAG, "VPN permission missing")
            return
        }
        val config = VyomVpnManager.getLastConfig(context) ?: return
        Log.i(TAG, "Auto-starting VPN on boot")
        VyomVpnManager.start(context, config)
    }
}