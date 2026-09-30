package com.example.vlessvpn

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import io.github.vyomtunnel.sdk.VyomVpnManager
import io.github.vyomtunnel.sdk.VyomState
import io.github.vyomtunnel.sdk.models.VyomIpInfo
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import com.example.vlessvpn.data.ConfigHistoryManager
import com.example.vlessvpn.data.ExternalSources
import io.github.vyomtunnel.sdk.models.TrafficProbeResult
import io.github.vyomtunnel.sdk.models.TrafficProbeStatus
import kotlinx.coroutines.sync.withLock

class MainViewModel(application: Application) : AndroidViewModel(application) {

    private companion object {
        const val MAX_SUBSCRIPTION_BYTES = 1 * 1024 * 1024
        const val EXTERNAL_CONFIG_BATCH_SIZE = 200
        const val EXTERNAL_TRAFFIC_BATCH_SIZE = 20
    }

    private val _vpnState = MutableStateFlow(VyomState.DISCONNECTED)
    val vpnState: StateFlow<VyomState> = _vpnState.asStateFlow()

    private val _totalUploadBytes = MutableStateFlow(0L)
    val totalUploadBytes: StateFlow<Long> = _totalUploadBytes.asStateFlow()

    private val _totalDownloadBytes = MutableStateFlow(0L)
    val totalDownloadBytes: StateFlow<Long> = _totalDownloadBytes.asStateFlow()

    private val _connectionDuration = MutableStateFlow(0L)
    val connectionDuration: StateFlow<Long> = _connectionDuration.asStateFlow()

    private val _exitIpInfo = MutableStateFlow<VyomIpInfo?>(null)
    val exitIpInfo: StateFlow<VyomIpInfo?> = _exitIpInfo.asStateFlow()

    private val _pingResults = MutableStateFlow<Map<String, Long>>(emptyMap())
    val pingResults: StateFlow<Map<String, Long>> = _pingResults.asStateFlow()

    private val _pingLoading = MutableStateFlow<Map<String, Boolean>>(emptyMap())
    val pingLoading: StateFlow<Map<String, Boolean>> = _pingLoading.asStateFlow()

    private val _isImporting = MutableStateFlow(false)
    val isImporting: StateFlow<Boolean> = _isImporting.asStateFlow()

    private val _uploadSpeed = MutableStateFlow(0L)
    val uploadSpeed: StateFlow<Long> = _uploadSpeed.asStateFlow()

    private val _downloadSpeed = MutableStateFlow(0L)
    val downloadSpeed: StateFlow<Long> = _downloadSpeed.asStateFlow()

    private val _vlessLink = MutableStateFlow("")
    val vlessLink: StateFlow<String> = _vlessLink.asStateFlow()

    private val _logs = MutableStateFlow<List<String>>(emptyList())
    val logs: StateFlow<List<String>> = _logs.asStateFlow()

    // Configuration History State
    private val _configHistory = MutableStateFlow<List<String>>(emptyList())
    val configHistory: StateFlow<List<String>> = _configHistory.asStateFlow()

    // Advanced Settings State
    private val _killSwitchEnabled = MutableStateFlow(false)
    val killSwitchEnabled: StateFlow<Boolean> = _killSwitchEnabled.asStateFlow()

    private val _autoStartEnabled = MutableStateFlow(false)
    val autoStartEnabled: StateFlow<Boolean> = _autoStartEnabled.asStateFlow()

    private val _autoReconnectEnabled = MutableStateFlow(false)
    val autoReconnectEnabled: StateFlow<Boolean> = _autoReconnectEnabled.asStateFlow()

    private val listener = object : VyomVpnManager.VyomListener {
        override fun onStateChanged(state: VyomState) {
            _vpnState.value = state
            if (state == VyomState.CONNECTED) {
                _totalUploadBytes.value = 0L
                _totalDownloadBytes.value = 0L
                startTimer()
                fetchExitIp()
            } else if (state == VyomState.DISCONNECTED || state == VyomState.ERROR || state == VyomState.IDLE) {
                stopTimer()
                _exitIpInfo.value = null
            }
        }

        override fun onTrafficUpdate(up: Long, down: Long) {
            _uploadSpeed.value = up
            _downloadSpeed.value = down
            if (_vpnState.value == VyomState.CONNECTED) {
                _totalUploadBytes.value += up
                _totalDownloadBytes.value += down
            }
        }

        override fun onLogReceived(message: String) {
            _logs.update { current ->
                val updated = current + message
                if (updated.size > 500) updated.drop(1) else updated
            }
        }
    }

    init {
        VyomVpnManager.initialize(application)
        VyomVpnManager.registerListener(application, listener)

        // Load initial state
        _killSwitchEnabled.value = VyomVpnManager.isKillSwitchEnabled(application)
        _autoStartEnabled.value = VyomVpnManager.isAutoStartEnabled(application)
        _autoReconnectEnabled.value = VyomVpnManager.isAutoReconnectEnabled(application)
        loadHistory()

        // Populate Vless link from latest history item if available
        val history = ConfigHistoryManager.getHistory(application)
        if (history.isNotEmpty()) {
            _vlessLink.value = history.first()
        }

        // Sync initial state if already connected
        val currentState = VyomVpnManager.currentState
        _vpnState.value = currentState
        if (currentState == VyomState.CONNECTED) {
            startTimer()
            fetchExitIp()
        }
    }

    override fun onCleared() {
        super.onCleared()
        VyomVpnManager.unregisterListener(getApplication())
        stopTimer()
    }

    fun updateVlessLink(link: String) {
        _vlessLink.value = link
    }

    fun loadHistory() {
        _configHistory.value = ConfigHistoryManager.getHistory(getApplication())
    }

    fun selectConfig(link: String) {
        _vlessLink.value = link
    }

    fun deleteConfigFromHistory(link: String) {
        ConfigHistoryManager.deleteFromHistory(getApplication(), link)
        loadHistory()
    }

    fun toggleKillSwitch(enabled: Boolean) {
        VyomVpnManager.setKillSwitch(getApplication(), enabled)
        _killSwitchEnabled.value = enabled
    }

    fun toggleAutoStart(enabled: Boolean) {
        VyomVpnManager.setAutoStartEnabled(getApplication(), enabled)
        _autoStartEnabled.value = enabled
    }

    fun toggleAutoReconnect(enabled: Boolean) {
        VyomVpnManager.setAutoReconnectEnabled(getApplication(), enabled)
        _autoReconnectEnabled.value = enabled
    }

    fun connect(activity: android.app.Activity) {
        val link = _vlessLink.value
        if (link.isNotBlank()) {
            VyomVpnManager.connectWithPermission(activity, link)
            ConfigHistoryManager.saveConfigToHistory(activity, link)
            loadHistory()
        }
    }

    fun disconnect() {
        VyomVpnManager.stop(getApplication())
    }

    private var timerJob: kotlinx.coroutines.Job? = null

    private fun startTimer() {
        timerJob?.cancel()
        _connectionDuration.value = 0L
        timerJob = viewModelScope.launch {
            while (isActive) {
                delay(1000L)
                _connectionDuration.value += 1L
            }
        }
    }

    private fun stopTimer() {
        timerJob?.cancel()
        timerJob = null
    }

    fun fetchExitIp() {
        _exitIpInfo.value = null
        VyomVpnManager.fetchIpInfo { info ->
            _exitIpInfo.value = info
        }
    }

    fun checkAllPings() {
        val list = _configHistory.value
        viewModelScope.launch {
            list.forEach { link ->
                launch {
                    checkServerPing(link)
                }
            }
        }
    }

    private val probeMutex = kotlinx.coroutines.sync.Mutex()
    private val _verificationStatuses = MutableStateFlow<Map<String, String>>(emptyMap())
    val verificationStatuses = _verificationStatuses.asStateFlow()
    private var externalCheckedAt = 0L
    private var externalAttemptAt = 0L
    fun shouldRefreshExternal() = !_externalLoading.value && _vpnState.value != VyomState.CONNECTED && _vpnState.value != VyomState.CONNECTING &&
        System.currentTimeMillis() - maxOf(externalCheckedAt, externalAttemptAt) >= 15 * 60 * 1000
    fun expireExternalResults() {
        if (externalCheckedAt > 0 && System.currentTimeMillis() - externalCheckedAt >= 15 * 60 * 1000)
            _verificationStatuses.update { it + _externalConfigs.value.associate { config -> config.first to "Проверка устарела" } }
    }

    private suspend fun probeTraffic(link: String): TrafficProbeResult = probeMutex.withLock {
        withContext(Dispatchers.IO) { VyomVpnManager.probeConfigTraffic(getApplication(), link) }
    }

    fun checkServerPing(link: String) {
        _pingLoading.update { it + (link to true) }
        viewModelScope.launch {
            try {
                val result = probeTraffic(link)
                _pingResults.update { it + (link to result.latency) }
                _verificationStatuses.update { it + (link to result.label) }
            } finally { _pingLoading.update { it + (link to false) } }
        }
    }
    private fun parseHostPort(link: String): Pair<String, Int>? = try {
        val config = org.json.JSONObject(io.github.vyomtunnel.sdk.utils.LinkParser.parse(link))
        when (config.optString("_protocol")) {
            "__mieru__" -> Pair(config.getString("server_host"), config.getInt("server_port"))
            "__hysteria2__" -> {
                val uri = java.net.URI("udp://" + config.getString("server"))
                Pair(uri.host, uri.port)
            }
            else -> {
                val settings = config.getJSONArray("outbounds").getJSONObject(0).getJSONObject("settings")
                val server = (settings.optJSONArray("vnext") ?: settings.getJSONArray("servers")).getJSONObject(0)
                Pair(server.getString("address"), server.getInt("port"))
            }
        }
    } catch (_: Exception) { null }
    fun importSubscription(url: String, context: android.content.Context, onComplete: (Int) -> Unit, onError: (String) -> Unit) {
        if (url.isBlank()) {
            onError("URL не может быть пустым")
            return
        }
        _isImporting.value = true
        viewModelScope.launch {
            try {
                val content = fetchSubscriptionContent(url)
                val links = parseSubscriptionContent(content)
                if (links.isEmpty()) {
                    withContext(Dispatchers.Main) {
                        onError("Не найдено подходящих ссылок (VLESS/Naive/Hysteria2)")
                    }
                } else {
                    withContext(Dispatchers.Main) {
                        var addedCount = 0
                        links.forEach { link ->
                            val duplicate = ConfigHistoryManager.findDuplicate(context, link)
                            if (duplicate == null) {
                                ConfigHistoryManager.saveConfigToHistory(context, link)
                                addedCount++
                            }
                        }
                        loadHistory()
                        if (addedCount > 0) {
                            onComplete(addedCount)
                        } else {
                            onError("Все конфигурации уже есть в списке")
                        }
                    }
                }
            } catch (e: Exception) {
                withContext(Dispatchers.Main) {
                    onError("Ошибка загрузки: ${e.message}")
                }
            } finally {
                _isImporting.value = false
            }
        }
    }

    private suspend fun fetchSubscriptionContent(url: String, maxBytes: Int = MAX_SUBSCRIPTION_BYTES): String = withContext(Dispatchers.IO) {
        val connection = java.net.URL(url).openConnection() as java.net.HttpURLConnection
        try {
            connection.connectTimeout = 10000
            connection.readTimeout = 10000
            connection.setRequestProperty("Cache-Control", "no-cache")
            connection.setRequestProperty("User-Agent", "AnariseVPN/1.4.16")
            connection.connect()
            if (connection.contentLengthLong > maxBytes) {
                throw java.io.IOException("Подписка превышает допустимый размер")
            }
            connection.inputStream.use { input ->
                val output = java.io.ByteArrayOutputStream()
                val buffer = ByteArray(8192)
                while (true) {
                    val count = input.read(buffer)
                    if (count < 0) break
                    if (output.size() + count > maxBytes) {
                        throw java.io.IOException("Подписка превышает допустимый размер")
                    }
                    output.write(buffer, 0, count)
                }
                output.toString(Charsets.UTF_8.name())
            }
        } finally {
            connection.disconnect()
        }
    }

    private fun parseSubscriptionContent(content: String): List<String> = ExternalSources.parse(content)

    fun checkForUpdates(currentVersion: String, onNewVersionAvailable: (String, String) -> Unit) {
        viewModelScope.launch(Dispatchers.IO) {
            try {
                val url = java.net.URL("https://api.github.com/repos/pasnya/anarise-vpn/releases/latest")
                val connection = url.openConnection() as java.net.HttpURLConnection
                connection.setRequestProperty("User-Agent", "VlessVPN-App")
                connection.connectTimeout = 5000
                connection.readTimeout = 5000

                if (connection.responseCode == 200) {
                    val responseText = connection.inputStream.bufferedReader().use { it.readText() }
                    val jsonObject = org.json.JSONObject(responseText)
                    
                    val latestTag = jsonObject.getString("tag_name").replace("v", "").trim()
                    val currentClean = currentVersion.replace("v", "").trim()

                    if (latestTag != currentClean) {
                        val assets = jsonObject.optJSONArray("assets")
                        val downloadUrl = if (assets != null && assets.length() > 0) {
                            assets.getJSONObject(0).getString("browser_download_url")
                        } else {
                            jsonObject.getString("html_url")
                        }

                        withContext(Dispatchers.Main) {
                            onNewVersionAvailable(latestTag, downloadUrl)
                        }
                    }
                }
            } catch (e: Exception) {
                e.printStackTrace()
            }
        }
    }

    private val _externalConfigs = MutableStateFlow<List<Pair<String, Long>>>(emptyList())
    val externalConfigs: StateFlow<List<Pair<String, Long>>> = _externalConfigs.asStateFlow()

    private val _externalLoading = MutableStateFlow(false)
    val externalLoading: StateFlow<Boolean> = _externalLoading.asStateFlow()

    private val _externalStatusText = MutableStateFlow("")
    val externalStatusText: StateFlow<String> = _externalStatusText.asStateFlow()

    fun fetchAndCheckExternalConfigs() {
        if (_externalLoading.value) return
        if (_vpnState.value == VyomState.CONNECTED || _vpnState.value == VyomState.CONNECTING) {
            _externalStatusText.value = "Отключите VPN перед проверкой внешнего списка"
            return
        }
        externalAttemptAt = System.currentTimeMillis()
        _externalLoading.value = true
        _externalConfigs.value = emptyList()
        _externalStatusText.value = "Загрузка свежих списков GitHub..."
        viewModelScope.launch {
            try {
                val sources = ExternalSources.load(getApplication())
                val sourceLists = coroutineScope {
                    sources.map { source -> async(Dispatchers.IO) {
                        var links: List<String> = emptyList()
                        for (url in listOf(source.url, source.mirrorUrl)) {
                            try {
                                links = ExternalSources.parse(fetchSubscriptionContent(url, 4 * 1024 * 1024))
                                if (links.isEmpty()) throw java.io.IOException("Нет поддерживаемых конфигураций")
                                _logs.update { (it + ("${source.name}: ${links.size} конфигураций" + if (url == source.url) "" else " (зеркало GitHub)")).takeLast(500) }
                                break
                            } catch (e: CancellationException) { throw e }
                            catch (e: Exception) {
                                _logs.update { (it + ("${source.name}: ошибка загрузки (${e.javaClass.simpleName})" + if (url == source.url) "; проверяю зеркало" else "")).takeLast(500) }
                            }
                        }
                        links
                    } }.awaitAll()
                }
                val loaded = sourceLists.count { it.isNotEmpty() }
                val uniqueLinks = ExternalSources.interleave(sourceLists)
                if (uniqueLinks.isEmpty()) {
                    _externalStatusText.value = "Не удалось получить поддерживаемые конфигурации: источников с ошибками ${sources.size - loaded}/${sources.size}. Подробности в логах."
                    _externalConfigs.value = emptyList()
                    return@launch
                }
                val completedCount = java.util.concurrent.atomic.AtomicInteger(0)
                var trafficChecks = 0
                val verified = mutableListOf<Pair<String, TrafficProbeResult>>()
                val failures = mutableMapOf<TrafficProbeStatus, Int>()
                val semaphore = kotlinx.coroutines.sync.Semaphore(30)
                for (batch in uniqueLinks.chunked(EXTERNAL_CONFIG_BATCH_SIZE)) {
                    _externalStatusText.value = "Проверка конфигураций (${completedCount.get()}/${uniqueLinks.size}); источников: $loaded/${sources.size}..."
                    val candidates = coroutineScope {
                        batch.map { link -> async(Dispatchers.IO) {
                            semaphore.acquire()
                            try {
                                // Only rank by TCP reachability. QUIC/UDP bypass
                                // the hint, and a failed TCP hint never discards a config.
                                val latency = if (link.startsWith("hysteria2://") || link.startsWith("hy2://")) 0L
                                    else {
                                        val hostPort = parseHostPort(link)
                                        if (hostPort == null) -1L else measureTcpLatency(hostPort.first, hostPort.second)
                                    }
                                val count = completedCount.incrementAndGet()
                                _externalStatusText.value = "Проверка конфигураций ($count/${uniqueLinks.size})..."
                                Pair(link, latency)
                            } finally { semaphore.release() }
                        } }.awaitAll()
                    }.sortedBy { if (it.second < 0) Long.MAX_VALUE else it.second }
                    for (trafficBatch in candidates.chunked(EXTERNAL_TRAFFIC_BATCH_SIZE)) {
                        for ((link, _) in trafficBatch) {
                            _externalStatusText.value = "Проверка HTTPS через VPN (проверено: $trafficChecks, найдено: ${verified.size})..."
                            val result = probeTraffic(link)
                            trafficChecks++
                            if (result.isUsable) {
                                _verificationStatuses.update { it + (link to result.label) }
                                verified.add(Pair(link, result))
                                _externalConfigs.value = verified.sortedBy { it.second.latency }.map { Pair(it.first, it.second.latency) }
                            } else failures[result.status] = (failures[result.status] ?: 0) + 1
                            if (result.status == TrafficProbeStatus.BUSY) throw IllegalStateException("VPN активен или локальный порт занят")
                        }
                        if (verified.isNotEmpty()) break
                    }
                    if (verified.isNotEmpty()) break
                }
                externalCheckedAt = verified.minOfOrNull { it.second.verifiedAt } ?: System.currentTimeMillis()
                _externalConfigs.value = verified.sortedBy { it.second.latency }.map { Pair(it.first, it.second.latency) }
                val full = verified.count { it.second.status == TrafficProbeStatus.VERIFIED }
                val partial = verified.size - full
                val localErrors = failures.filterKeys { it == TrafficProbeStatus.MISSING_ENGINE || it == TrafficProbeStatus.ENGINE_START_FAILED }.values.sum()
                if (failures.isNotEmpty()) _logs.update { (it + ("Результаты проверки: " + failures.entries.joinToString { (status, count) -> "${TrafficProbeResult(status).label}: $count" })).takeLast(500) }
                val status = if (verified.isNotEmpty()) "Трафик подтверждён: $full, частичная доступность: $partial"
                    else if (localErrors > 0) "Трафик не подтверждён; ошибок проверки: $localErrors. Подробности в логах."
                    else "Рабочий HTTPS-трафик не подтверждён"
                _externalStatusText.value = "$status (проверено: $trafficChecks/${uniqueLinks.size}; источников: $loaded/${sources.size})"
            } catch (e: CancellationException) { throw e }
            catch (e: Exception) {
                _externalStatusText.value = "Ошибка проверки внешних списков: ${e.message}"
            } finally { _externalLoading.value = false }
        }
    }

    private suspend fun measureTcpLatency(host: String, port: Int): Long = withContext(Dispatchers.IO) {
        val start = android.os.SystemClock.elapsedRealtime()
        try {
            java.net.Socket().use { it.connect(java.net.InetSocketAddress(host, port), 2500) }
            android.os.SystemClock.elapsedRealtime() - start
        } catch (_: Exception) { -1L }
    }
}

