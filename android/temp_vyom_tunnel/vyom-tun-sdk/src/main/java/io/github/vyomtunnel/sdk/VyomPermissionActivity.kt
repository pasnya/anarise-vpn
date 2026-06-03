package io.github.vyomtunnel.sdk

import android.net.VpnService
import android.os.Build
import android.os.Bundle
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity

class VyomPermissionActivity : AppCompatActivity() {

    private val requestVpn = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) {
        if (it.resultCode == RESULT_OK) {
            startVpnIfConfigExists()
        }
        finish()
    }

    private val requestNotification = registerForActivityResult(ActivityResultContracts.RequestPermission()) {
        requestVpnPermission()
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            requestNotification.launch(android.Manifest.permission.POST_NOTIFICATIONS)
        } else {
            requestVpnPermission()
        }
    }

    private fun requestVpnPermission() {
        val intent = VpnService.prepare(this)
        if (intent != null) {
            requestVpn.launch(intent)
        } else {
            startVpnIfConfigExists()
            finish()
        }
    }

    private fun startVpnIfConfigExists() {
        val lastConfig = VyomVpnManager.getLastConfig(this)
        if (lastConfig != null) {
            VyomVpnManager.start(this, lastConfig)
        }
    }
}