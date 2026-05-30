package io.github.vyomtunnel

import android.content.Intent
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import android.util.Log
import android.widget.*
import androidx.annotation.RequiresApi
import androidx.appcompat.app.AppCompatActivity
import io.github.vyomtunnel.sdk.VyomState
import io.github.vyomtunnel.sdk.VyomVpnManager
import io.github.vyomtunnel.sdk.utils.TrafficFormatter
import io.github.vyomtunnel.sdk.utils.VyomConfigBuilder
import androidx.appcompat.widget.SwitchCompat

class MainActivity : AppCompatActivity() {

    private lateinit var etConfig: EditText
    private lateinit var etManualHost: EditText
    private lateinit var etManualUuid: EditText
    private lateinit var btnConnect: Button
    private lateinit var tvState: TextView
    private lateinit var tvSpeedUp: TextView
    private lateinit var tvSpeedDown: TextView
    private lateinit var tvIpInfo: TextView
    private lateinit var tvQuality: TextView
    private lateinit var swKillSwitch: SwitchCompat

    @RequiresApi(Build.VERSION_CODES.O)
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        // SDK Initialize: Loads native cores and copies routing assets
        VyomVpnManager.initialize(this)

        initViews()
        attachListeners()
    }

    private fun initViews() {
        etConfig = findViewById(R.id.etConfig)
        etManualHost = findViewById(R.id.etManualHost)
        etManualUuid = findViewById(R.id.etManualUuid)
        btnConnect = findViewById(R.id.btnConnect)
        tvState = findViewById(R.id.tvState)
        tvSpeedUp = findViewById(R.id.tvSpeedUp)
        tvSpeedDown = findViewById(R.id.tvSpeedDown)
        tvIpInfo = findViewById(R.id.tvIpInfo)
        tvQuality = findViewById(R.id.tvQuality)
        swKillSwitch = findViewById(R.id.swKillSwitch)

        swKillSwitch.isChecked = VyomVpnManager.isKillSwitchEnabled(this)
    }

    @RequiresApi(Build.VERSION_CODES.O)
    private fun attachListeners() {
        // 1. Connect via Link or JSON
        btnConnect.setOnClickListener {
            val input = etConfig.text.toString()
            if (input.isNotBlank()) VyomVpnManager.connectWithPermission(this, input)
        }

        // 2. Connect via Manual Config Builder (SDK DSL)
        findViewById<Button>(R.id.btnBuildAndConnect).setOnClickListener {
            val host = etManualHost.text.toString().trim()
            val uuid = etManualUuid.text.toString().trim()

            if (host.isEmpty() || uuid.isEmpty()) return@setOnClickListener

            // Using the SDK ConfigBuilder
            val config = VyomConfigBuilder()
                .vless()
                .server(host, 443)
                .credentials(uuid)
                .reality("rRGUNyr-7bb953ApQi4bq33_CTOfiNZ9LJcSaaWTriE", "9e90371fd95824f2")
                .build()

            VyomVpnManager.connectWithPermission(this, config)
        }

        findViewById<Button>(R.id.btnStop).setOnClickListener { VyomVpnManager.stop(this) }

        // 3. IP Info Check
        findViewById<Button>(R.id.btnRefreshIp).setOnClickListener {
            tvIpInfo.text = "Fetching location data..."

            VyomVpnManager.fetchIpInfo { info ->
                runOnUiThread {
                    if (info != null) {
                        // Example: 78.$$$.2$8.6$ | London, United Kingdom
                        tvIpInfo.text = "${info.ip} | ${info.city}, ${info.country}"

                        // You can also show the ISP as a Toast
                        Toast.makeText(this, "Provider: ${info.isp}", Toast.LENGTH_SHORT).show()
                    } else {
                        tvIpInfo.text = "Failed to fetch IP info"
                    }
                }
            }
        }

        findViewById<SwitchCompat>(R.id.swAutoStart).isChecked = VyomVpnManager.isAutoStartEnabled(this)
        findViewById<SwitchCompat>(R.id.swAutoReconnect).isChecked = VyomVpnManager.isAutoReconnectEnabled(this)

        findViewById<SwitchCompat>(R.id.swAutoStart).setOnCheckedChangeListener { _, enabled ->
            VyomVpnManager.setAutoStartEnabled(this, enabled)
        }

        findViewById<SwitchCompat>(R.id.swAutoReconnect).setOnCheckedChangeListener { _, enabled ->
            VyomVpnManager.setAutoReconnectEnabled(this, enabled)
        }

        findViewById<Button>(R.id.btnSplit).setOnClickListener {
            startActivity(Intent(this, AppSelectorActivity::class.java))
        }

        swKillSwitch.setOnCheckedChangeListener { _, isChecked ->
            VyomVpnManager.setKillSwitch(this, isChecked)
        }

        findViewById<Button>(R.id.btnDiag).setOnClickListener {
            VyomVpnManager.getPerformanceProfile { profile ->
                runOnUiThread {
                    tvQuality.text = "Quality: ${profile.qualityScore}% | Jitter: ${profile.jitter}ms | Loss: ${profile.packetLoss}%"
                }
            }
        }

        // Cross-process updates for State and Speed
        VyomVpnManager.registerListener(this, object : VyomVpnManager.VyomListener {
            override fun onStateChanged(state: VyomState) {
                runOnUiThread {
                    tvState.text = state.name
                    btnConnect.isEnabled = (state == VyomState.IDLE || state == VyomState.DISCONNECTED || state == VyomState.ERROR)
                    tvState.setTextColor(if (state == VyomState.CONNECTED) 0xFF4CAF50.toInt() else 0xFF9E9E9E.toInt())
                }
            }

            override fun onTrafficUpdate(up: Long, down: Long) {
                runOnUiThread {
                    tvSpeedUp.text = "↑ ${TrafficFormatter.formatSpeed(up)}"
                    tvSpeedDown.text = "↓ ${TrafficFormatter.formatSpeed(down)}"
                }
            }

            override fun onLogReceived(message: String) {
                Log.d("VyomSDK_Log", message)
            }

        })
    }

    override fun onDestroy() {
        VyomVpnManager.unregisterListener(this)
        super.onDestroy()
    }
}