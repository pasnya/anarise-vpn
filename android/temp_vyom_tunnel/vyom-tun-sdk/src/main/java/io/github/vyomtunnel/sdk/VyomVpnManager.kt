package io.github.vyomtunnel.sdk

import android.app.Activity
import android.app.Application
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.VpnService
import android.os.Build
import android.util.Log
import android.webkit.WebView
import android.widget.Toast
import io.github.vyomtunnel.core.NativeEngine
import io.github.vyomtunnel.sdk.models.VyomIpInfo
import io.github.vyomtunnel.sdk.utils.AssetUtils
import io.github.vyomtunnel.sdk.models.TrafficProbeResult
import io.github.vyomtunnel.sdk.models.TrafficProbeStatus
import io.github.vyomtunnel.sdk.utils.MieruEngine
import io.github.vyomtunnel.sdk.utils.HysteriaEngine
import io.github.vyomtunnel.sdk.utils.LinkParser
import io.github.vyomtunnel.sdk.utils.VyomLogger

object VyomVpnManager {

    private const val TAG = "VyomVpnManager"

    // Broadcast Actions
    const val ACTION_VPN_STATE = "io.github.vyomtunnel.VPN_STATE"
    const val ACTION_VPN_TRAFFIC = "io.github.vyomtunnel.VPN_TRAFFIC"
    const val ACTION_SDK_LOGS = "io.github.vyomtunnel.SDK_LOGS"

    // Persistence Keys
    private const val PREFS_NAME = "vyom_vpn_prefs"
    private const val KEY_LAST_CONFIG = "last_config"
    private const val KEY_VPN_ALIVE = "vpn_should_be_running"
    private const val KEY_AUTO_START = "auto_start_on_boot"
    private const val KEY_AUTO_RECONNECT = "auto_reconnect_on_network"
    private const val KEY_EXCLUDED_APPS = "excluded_apps_list"
    private const val KEY_CUSTOM_NAME = "custom_app_name"
    private const val KEY_CUSTOM_ICON = "custom_app_icon"

    private var isInitialized = false
    private var internalReceiver: BroadcastReceiver? = null
    private val vpnListeners = java.util.concurrent.CopyOnWriteArraySet<VyomListener>()
    private var notificationConfig = VyomNotificationConfig()
    private val excludedApps = mutableSetOf<String>()
    private const val KEY_KILL_SWITCH = "kill_switch_enabled"
    const val ACTION_NO_INTERNET = "io.github.vyomtunnel.NO_INTERNET"
    private const val LOCAL_SOCKS_PORT = 20808
    private val configVerificationLock = Any()

    @Volatile var currentState: VyomState = VyomState.IDLE
        private set

    /**
     * Interface for the host application to receive updates.
     */
    interface VyomListener {
        fun onStateChanged(state: VyomState)
        fun onTrafficUpdate(up: Long, down: Long)
        fun onLogReceived(message: String)
    }

    /**
     * Data class for UI branding of the VPN notification.
     */
    data class VyomNotificationConfig(
        val title: String? = null,
        val content: String? = null,
        val iconResId: Int? = null,
        val channelName: String = "VPN Service"
    )

    // --- CORE API ---

    fun initialize(context: Context) {
        if (isInitialized) return
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            val processName = Application.getProcessName()
            if (context.packageName != processName) {
                try {
                    WebView.setDataDirectorySuffix("vyom_process")
                } catch (e: Exception) {
                    VyomLogger.i(context, "WebView suffix already set")
                }
            }
        }

        // Asset copying, native loading and preference access all have device-specific
        // failure modes (no free space, stripped ABI, locked storage). None of them may
        // take the whole app down, so every Throwable is reported and swallowed here.
        try {
            AssetUtils.copyAssets(context)
            System.loadLibrary("xray")
            System.loadLibrary("vyom-v2ray")
            loadSavedExclusions(context)
            isInitialized = true
        } catch (e: Throwable) {
            VyomLogger.e(context, "VPN engine initialization failed", e)
        }
    }

    fun connect(context: Context, linkOrJson: String): String? {
        return try {
            val finalConfig = if (linkOrJson.trim().startsWith("{")) {
                linkOrJson
            } else {
                LinkParser.parse(linkOrJson)
            }

            val validationError = validateConfig(context, finalConfig)
            if (validationError != null) return validationError

            start(context, finalConfig)
            null // Success
        } catch (e: Throwable) {
            e.message ?: "Unknown error"
        }
    }

    /**
     * Entry point for the UI. Everything it touches — link parsing, the native
     * validator (which can throw UnsatisfiedLinkError, not an Exception), the keystore
     * and starting an activity or service — can fail on a real device, so the whole
     * body is guarded: the user gets a message instead of a crash.
     */
    fun connectWithPermission(activity: Activity, input: String) {
        try {
            val finalConfig = try {
                if (input.trim().startsWith("{")) input
                else LinkParser.parse(input)
            } catch (e: Throwable) {
                Toast.makeText(activity, "Некорректная ссылка или JSON", Toast.LENGTH_LONG).show()
                return
            }

            val validationError = validateConfig(activity, finalConfig)
            if (validationError != null) {
                Toast.makeText(activity, validationError, Toast.LENGTH_LONG).show()
                VyomLogger.e(activity, "Config validation failed: $validationError", null)
                return
            }

            val vpnIntent = VpnService.prepare(activity)

            val hasNotificationPermission = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                activity.checkSelfPermission(android.Manifest.permission.POST_NOTIFICATIONS) == android.content.pm.PackageManager.PERMISSION_GRANTED
            } else {
                true
            }
            if (vpnIntent != null || !hasNotificationPermission) {
                saveConfig(activity, finalConfig)
                activity.startActivity(
                    Intent(activity, VyomPermissionActivity::class.java)
                )
            } else {
                start(activity, finalConfig)
            }
        } catch (e: Throwable) {
            VyomLogger.e(activity, "Connect failed", e)
            runCatching {
                Toast.makeText(activity, "Не удалось подключиться: ${e.message ?: e.javaClass.simpleName}", Toast.LENGTH_LONG).show()
            }
        }
    }

    /**
     * Starts the VPN service. Callers include the quick-settings tile and the boot
     * receiver, where a foreground-service start can be rejected by the system; such a
     * refusal is reported as an ERROR state rather than crashing the caller.
     */
    fun start(context: Context, configJson: String) {
        try {
            saveConfig(context, configJson)
            setVpnShouldRun(context, true)

            val intent = Intent(context, VyomVpnService::class.java).apply {
                action = "START_VPN"
                putExtra(VyomVpnService.EXTRA_CONFIG, configJson)
                putExtra(VyomVpnService.NOTIF_TITLE, notificationConfig.title)
                putExtra(VyomVpnService.NOTIF_CONTENT, notificationConfig.content)
                putExtra(VyomVpnService.NOTIF_ICON, notificationConfig.iconResId ?: 0)
                putExtra(VyomVpnService.NOTIF_CHANNEL, notificationConfig.channelName)
            }

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        } catch (e: Throwable) {
            VyomLogger.e(context, "Failed to start the VPN service", e)
            currentState = VyomState.ERROR
            vpnListeners.forEach { runCatching { it.onStateChanged(VyomState.ERROR) } }
        }
    }

    fun stop(context: Context) {
        try {
            setVpnShouldRun(context, false)
            val intent = Intent(context, VyomVpnService::class.java).apply {
                action = "STOP_VPN"
            }
            context.startService(intent)
        } catch (e: Throwable) {
            VyomLogger.e(context, "Failed to stop the VPN service", e)
        }
    }

    fun validateConfig(context: Context, config: String): String? {
        if (LinkParser.isMieruConfig(config)) {
            return try {
                val obj = org.json.JSONObject(config)
                if (obj.optString("server").isNullOrBlank()) {
                    "Mieru server address is missing"
                } else if (obj.optString("username").isNullOrBlank()) {
                    "Mieru username is missing"
                } else if (obj.optString("password").isNullOrBlank()) {
                    "Mieru password is missing"
                } else {
                    null // Valid config
                }
            } catch (e: Exception) {
                "Invalid Mieru JSON config: ${e.message}"
            }
        }
        if (LinkParser.isHysteria2Config(config)) {
            return try {
                val obj = org.json.JSONObject(config)
                if (obj.optString("server").isNullOrBlank()) {
                    "Hysteria2 server address is missing"
                } else if (obj.optString("auth").isNullOrBlank()) {
                    "Hysteria2 auth password is missing"
                } else {
                    null // Valid config
                }
            } catch (e: Exception) {
                "Invalid Hysteria2 JSON config: ${e.message}"
            }
        }
        val assetPath = context.filesDir.absolutePath
        // The validator is a JNI call: a missing or mismatched .so throws
        // UnsatisfiedLinkError, which is an Error and would escape a plain catch.
        return try {
            NativeEngine.validateConfig(config, assetPath)
        } catch (e: Throwable) {
            VyomLogger.e(context, "Native config validation failed", e)
            "Движок VPN недоступен на этом устройстве"
        }
    }

    /**
     * Verifies that a link can carry real HTTPS traffic, not merely accept a TCP
     * connection. The core uses a fixed local SOCKS port, so this is intentionally
     * serialized and must never run while the user's VPN session is active.
     */
    private val probeExecutor = java.util.concurrent.Executors.newFixedThreadPool(3)

    fun verifyConfigTraffic(context: Context, link: String): Boolean = probeConfigTraffic(context, link).isUsable

    fun probeConfigTraffic(context: Context, link: String): TrafficProbeResult = synchronized(configVerificationLock) {
        if (currentState == VyomState.CONNECTED || currentState == VyomState.CONNECTING || currentState == VyomState.STOPPING)
            return@synchronized TrafficProbeResult(TrafficProbeStatus.BUSY)
        // A fixed SOCKS listener is shared with the VPN service. Never test
        // through a listener left by another session or stop an active session.
        try {
            java.net.ServerSocket().use { it.bind(java.net.InetSocketAddress("127.0.0.1", LOCAL_SOCKS_PORT)) }
        } catch (_: Exception) { return@synchronized TrafficProbeResult(TrafficProbeStatus.BUSY) }

        val config = try { LinkParser.parse(link) }
        catch (_: Exception) { return@synchronized TrafficProbeResult(TrafficProbeStatus.INVALID_CONFIG) }
        val isHysteria = LinkParser.isHysteria2Config(config)
        val isMieru = LinkParser.isMieruConfig(config)
        var started = false
        try {
            val params = org.json.JSONObject(config)
            if (isHysteria || isMieru) {
                val library = if (isHysteria) "libhysteria2.so" else "libmieru.so"
                if (!java.io.File(context.applicationInfo.nativeLibraryDir, library).exists())
                    return@synchronized TrafficProbeResult(TrafficProbeStatus.MISSING_ENGINE)
            }
            started = true
            when {
                isHysteria -> HysteriaEngine.start(context,
                    server = params.getString("server"), auth = params.getString("auth"),
                    sni = params.optString("sni"), insecure = params.optBoolean("insecure", false),
                    obfsType = params.optString("obfs_type"), obfsPassword = params.optString("obfs_password"), alpn = params.optString("alpn"))
                isMieru -> MieruEngine.start(context,
                    server = params.getString("server"), username = params.getString("username"), password = params.getString("password"),
                    multiplexing = params.optString("multiplexing", "MULTIPLEXING_LOW"), transport = params.optString("transport", "TCP"),
                    rpcPort = java.net.ServerSocket(0).use { it.localPort })
                else -> {
                    AssetUtils.copyAssets(context)
                    params.put("routing", org.json.JSONObject().put("domainStrategy", "AsIs").put("rules", org.json.JSONArray().put(
                        org.json.JSONObject().put("type", "field").put("network", "tcp,udp").put("outboundTag", "proxy"))))
                    val probeConfig = params.toString()
                    if (NativeEngine.validateConfig(probeConfig, context.filesDir.absolutePath) != null)
                        return@synchronized TrafficProbeResult(TrafficProbeStatus.INVALID_CONFIG)
                    NativeEngine.startXray(probeConfig, context.filesDir.absolutePath)
                }
            }
            val deadline = android.os.SystemClock.elapsedRealtime() + 5000
            var ready = false
            while (android.os.SystemClock.elapsedRealtime() < deadline) {
                try {
                    java.net.Socket().use { it.connect(java.net.InetSocketAddress("127.0.0.1", LOCAL_SOCKS_PORT), 200) }
                    ready = true; break
                } catch (_: Exception) { Thread.sleep(100) }
            }
            if (!ready) return@synchronized TrafficProbeResult(TrafficProbeStatus.ENGINE_START_FAILED)

            val endpoints = listOf(
                Triple("https://www.gstatic.com/generate_204", 204, ""),
                Triple("https://cp.cloudflare.com/generate_204", 204, ""),
                Triple("https://detectportal.firefox.com/success.txt", 200, "success")
            )
            val passed = mutableSetOf<String>()
            val delays = mutableListOf<Long>()
            var lastFailure = TrafficProbeStatus.NETWORK_ERROR
            for (attempt in 0..1) {
                val tasks = endpoints.map { target -> java.util.concurrent.Callable {
                    val proxy = java.net.Proxy(java.net.Proxy.Type.SOCKS, java.net.InetSocketAddress("127.0.0.1", LOCAL_SOCKS_PORT))
                    val timer = android.os.SystemClock.elapsedRealtime()
                    val connection = java.net.URL(target.first).openConnection(proxy) as java.net.HttpURLConnection
                    connection.connectTimeout = 4000; connection.readTimeout = 4000
                    connection.instanceFollowRedirects = false; connection.useCaches = false
                    connection.setRequestProperty("Cache-Control", "no-cache, no-store")
                    try {
                        val code = connection.responseCode
                        var body = ""
                        if (code == 200) {
                            connection.inputStream.use { input ->
                                val bytes = ByteArray(65)
                                var total = 0
                                while (total < bytes.size) {
                                    val read = input.read(bytes, total, bytes.size - total)
                                    if (read < 0) break
                                    total += read
                                }
                                body = String(bytes, 0, total, Charsets.UTF_8).trim()
                            }
                        }
                        val ok = TrafficProbeResult.expectedResponse(target.second, target.third, code, body)
                        Triple(target.first, if (ok) android.os.SystemClock.elapsedRealtime() - timer else -1L,
                            if (ok) TrafficProbeStatus.VERIFIED else TrafficProbeStatus.UNEXPECTED_RESPONSE)
                    } catch (_: java.net.SocketTimeoutException) { Triple(target.first, -1L, TrafficProbeStatus.TIMEOUT) }
                    catch (_: Exception) { Triple(target.first, -1L, TrafficProbeStatus.NETWORK_ERROR) }
                    finally { connection.disconnect() }
                } }
                val futures = probeExecutor.invokeAll(tasks, 9, java.util.concurrent.TimeUnit.SECONDS)
                for (future in futures) {
                    if (future.isCancelled) { lastFailure = TrafficProbeStatus.TIMEOUT; continue }
                    val (url, latency, status) = future.get()
                    if (latency >= 0) { passed.add(url); delays.add(latency) } else lastFailure = status
                }
                val success = TrafficProbeResult.evaluateSuccess(passed, delays, attempt == 1)
                if (success != null) return@synchronized success
                Thread.sleep(150)
            }
            TrafficProbeResult(lastFailure)
        } catch (_: UnsatisfiedLinkError) {
            TrafficProbeResult(TrafficProbeStatus.MISSING_ENGINE)
        } catch (_: Exception) {
            TrafficProbeResult(TrafficProbeStatus.ENGINE_START_FAILED)
        } finally {
            if (started) runCatching {
                when { isHysteria -> HysteriaEngine.stop(); isMieru -> MieruEngine.stop(); else -> NativeEngine.stopXray() }
            }
        }
    }
    // --- LISTENERS & IPC (Inter-Process Communication) ---

    fun registerListener(context: Context, listener: VyomListener) {
        this.vpnListeners.add(listener)
        if (internalReceiver != null) return

        internalReceiver = object : BroadcastReceiver() {
            // A receiver runs on the main thread: anything thrown here kills the app,
            // so an unknown state name or a misbehaving listener is contained.
            override fun onReceive(context: Context?, intent: Intent?) {
                try {
                    when (intent?.action) {
                        ACTION_VPN_STATE -> {
                            val stateName = intent.getStringExtra("STATE") ?: return
                            val state = VyomState.entries.firstOrNull { it.name == stateName } ?: return
                            currentState = state
                            vpnListeners.forEach { runCatching { it.onStateChanged(state) } }
                        }
                        ACTION_VPN_TRAFFIC -> {
                            val up = intent.getLongExtra("UP", 0L)
                            val down = intent.getLongExtra("DOWN", 0L)
                            vpnListeners.forEach { runCatching { it.onTrafficUpdate(up, down) } }
                        }
                        ACTION_SDK_LOGS -> {
                            val msg = intent.getStringExtra("MSG") ?: ""
                            vpnListeners.forEach { runCatching { it.onLogReceived(msg) } }
                        }
                    }
                } catch (e: Throwable) {
                    Log.e(TAG, "Failed to handle ${intent?.action}", e)
                }
            }
        }

        val filter = IntentFilter().apply {
            addAction(ACTION_VPN_STATE)
            addAction(ACTION_VPN_TRAFFIC)
            addAction(ACTION_SDK_LOGS)
        }

        val senderPermission = "${context.packageName}.permission.INTERNAL_VPN_EVENTS"
        val appContext = context.applicationContext
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                appContext.registerReceiver(
                    internalReceiver,
                    filter,
                    senderPermission,
                    null,
                    Context.RECEIVER_NOT_EXPORTED
                )
            } else {
                appContext.registerReceiver(internalReceiver, filter, senderPermission, null)
            }
        } catch (e: Throwable) {
            // Without the receiver the UI stops getting state updates, but the app
            // keeps working; a registration failure is not worth a crash.
            Log.e(TAG, "Failed to register the VPN event receiver", e)
            internalReceiver = null
        }
    }

    fun unregisterListener(context: Context, listener: VyomListener? = null) {
        if (listener != null) {
            vpnListeners.remove(listener)
        } else {
            vpnListeners.clear()
        }
        if (vpnListeners.isEmpty()) {
            internalReceiver?.let {
                runCatching { context.applicationContext.unregisterReceiver(it) }
                internalReceiver = null
            }
        }
    }

    // --- APP SELECTION (SPLIT TUNNELING) ---

    fun toggleAppExclusion(context: Context, packageName: String) {
        if (excludedApps.contains(packageName)) excludedApps.remove(packageName)
        else excludedApps.add(packageName)

        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).edit()
            .putStringSet(KEY_EXCLUDED_APPS, excludedApps).apply()
    }

    fun getExcludedApps(context: Context): Set<String> {
        if (excludedApps.isEmpty()) loadSavedExclusions(context)
        return excludedApps
    }

    private fun loadSavedExclusions(context: Context) {
        val saved = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .getStringSet(KEY_EXCLUDED_APPS, emptySet()) ?: emptySet()
        excludedApps.clear()
        excludedApps.addAll(saved)
    }

    // --- DIAGNOSTICS & HELPERS ---



    fun setNotificationConfig(config: VyomNotificationConfig) {
        this.notificationConfig = config
    }

    // --- PERSISTENCE HELPERS ---

    private fun saveConfig(context: Context, config: String) {
        SecureConfigStore.put(context, config)
    }

    private fun setVpnShouldRun(context: Context, shouldRun: Boolean) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).edit()
            .putBoolean(KEY_VPN_ALIVE, shouldRun).apply()
    }

    fun getLastConfig(context: Context): String? =
        SecureConfigStore.get(context) ?: migrateLegacyConfig(context)

    private fun migrateLegacyConfig(context: Context): String? {
        val prefs = context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
        val legacy = prefs.getString(KEY_LAST_CONFIG, null) ?: return null
        SecureConfigStore.put(context, legacy)
        prefs.edit().remove(KEY_LAST_CONFIG).apply()
        return legacy
    }

    fun wasVpnRunning(context: Context): Boolean =
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).getBoolean(KEY_VPN_ALIVE, false)

    fun setKillSwitch(context: Context, enabled: Boolean) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .edit().putBoolean(KEY_KILL_SWITCH, enabled).apply()
    }

    fun isKillSwitchEnabled(context: Context): Boolean {
        return context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .getBoolean(KEY_KILL_SWITCH, false)
    }

    fun fetchIpInfo(callback: (VyomIpInfo?) -> Unit) {
        kotlin.concurrent.thread {
            try {
                val proxy = java.net.Proxy(
                    java.net.Proxy.Type.SOCKS,
                    java.net.InetSocketAddress("127.0.0.1", 20808)
                )

                val url = java.net.URL("https://ipwho.is/")
                val conn = url.openConnection(proxy) as java.net.HttpURLConnection
                conn.connectTimeout = 5000
                conn.readTimeout = 5000

                val response = conn.inputStream.bufferedReader().use { it.readText() }

                val info = VyomIpInfo.fromJson(response)
                callback(info)
            } catch (e: Exception) {
                Log.e(TAG, "Failed to fetch IP info: ${e.message}")
                callback(null)
            }
        }
    }

    fun isPermissionGranted(context: Context): Boolean {
        return android.net.VpnService.prepare(context) == null
    }

    /**
     * True when the system reports an active VPN transport. The service lives in its
     * own process and announces state over broadcasts; if the UI process starts (or
     * restarts) mid-session it has missed them, so this asks the OS directly.
     */
    fun isTunnelActive(context: Context): Boolean = try {
        val manager = context.getSystemService(Context.CONNECTIVITY_SERVICE) as android.net.ConnectivityManager
        manager.allNetworks.any { network ->
            manager.getNetworkCapabilities(network)
                ?.hasTransport(android.net.NetworkCapabilities.TRANSPORT_VPN) == true
        }
    } catch (e: Throwable) {
        Log.e(TAG, "Failed to read the VPN transport state", e)
        false
    }

    fun setAutoStartEnabled(context: Context, enabled: Boolean) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .edit().putBoolean(KEY_AUTO_START, enabled).apply()
    }

    fun isAutoStartEnabled(context: Context): Boolean =
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .getBoolean(KEY_AUTO_START, false)

    fun setAutoReconnectEnabled(context: Context, enabled: Boolean) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .edit().putBoolean(KEY_AUTO_RECONNECT, enabled).apply()
    }

    fun isAutoReconnectEnabled(context: Context): Boolean =
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .getBoolean(KEY_AUTO_RECONNECT, false)

    fun setAppName(context: Context, name: String) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .edit().putString(KEY_CUSTOM_NAME, name).apply()
    }

    fun setAppIcon(context: Context, iconResId: Int) {
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE)
            .edit().putInt(KEY_CUSTOM_ICON, iconResId).apply()
    }

    internal fun getCustomName(context: Context): String? =
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).getString(KEY_CUSTOM_NAME, null)

    internal fun getCustomIcon(context: Context): Int =
        context.getSharedPreferences(PREFS_NAME, Context.MODE_PRIVATE).getInt(KEY_CUSTOM_ICON, 0)
}
