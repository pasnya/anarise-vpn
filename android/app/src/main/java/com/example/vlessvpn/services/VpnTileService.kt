package com.example.vlessvpn.services

import android.content.Intent
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import io.github.vyomtunnel.sdk.VyomState
import io.github.vyomtunnel.sdk.VyomVpnManager

class VpnTileService : TileService() {

    private val vpnListener = object : VyomVpnManager.VyomListener {
        override fun onStateChanged(state: VyomState) {
            updateTileState(state)
        }
        override fun onTrafficUpdate(up: Long, down: Long) {}
        override fun onLogReceived(message: String) {}
    }

    override fun onStartListening() {
        super.onStartListening()
        VyomVpnManager.initialize(applicationContext)
        VyomVpnManager.registerListener(applicationContext, vpnListener)
        updateTileState(VyomVpnManager.currentState)
    }

    override fun onStopListening() {
        super.onStopListening()
        VyomVpnManager.unregisterListener(applicationContext)
    }

    override fun onClick() {
        super.onClick()
        val state = VyomVpnManager.currentState
        if (state == VyomState.CONNECTED) {
            VyomVpnManager.stop(applicationContext)
        } else if (state == VyomState.DISCONNECTED || state == VyomState.ERROR || state == VyomState.IDLE) {
            val lastConfig = VyomVpnManager.getLastConfig(applicationContext)
            if (lastConfig != null) {
                // Connect with permission or start directly. If permission is already granted, start works.
                // If not, we should launch the MainActivity to guide the user.
                if (VyomVpnManager.isPermissionGranted(applicationContext)) {
                    VyomVpnManager.start(applicationContext, lastConfig)
                } else {
                    launchActivity()
                }
            } else {
                launchActivity()
            }
        }
    }

    private fun launchActivity() {
        val intent = packageManager.getLaunchIntentForPackage(packageName)?.apply {
            addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        if (intent != null) {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
                val pendingIntent = android.app.PendingIntent.getActivity(
                    this, 0, intent, android.app.PendingIntent.FLAG_IMMUTABLE
                )
                startActivityAndCollapse(pendingIntent)
            } else {
                @Suppress("DEPRECATION")
                startActivityAndCollapse(intent)
            }
        }
    }

    private fun updateTileState(state: VyomState) {
        val tile = qsTile ?: return
        when (state) {
            VyomState.CONNECTED -> {
                tile.state = Tile.STATE_ACTIVE
                tile.label = "ANARISE Connected"
            }
            VyomState.CONNECTING -> {
                tile.state = Tile.STATE_ACTIVE
                tile.label = "ANARISE Connecting..."
            }
            VyomState.STOPPING -> {
                tile.state = Tile.STATE_ACTIVE
                tile.label = "ANARISE Disconnecting..."
            }
            else -> {
                tile.state = Tile.STATE_INACTIVE
                tile.label = "ANARISE"
            }
        }
        tile.updateTile()
    }
}
