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
import com.example.vlessvpn.data.ConfigHistoryManager

class MainViewModel(application: Application) : AndroidViewModel(application) {

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
            val current = _logs.value.toMutableList()
            current.add(message)
            if (current.size > 500) {
                current.removeAt(0)
            }
            _logs.value = current
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

    fun checkServerPing(link: String) {
        _pingLoading.value = _pingLoading.value + (link to true)
        viewModelScope.launch {
            val hostPort = parseHostPort(link)
            val latency = if (hostPort != null) {
                runTcpPing(hostPort.first, hostPort.second)
            } else {
                -1L
            }
            _pingResults.value = _pingResults.value + (link to latency)
            _pingLoading.value = _pingLoading.value + (link to false)
        }
    }

    private fun parseHostPort(link: String): Pair<String, Int>? {
        try {
            val uri = android.net.Uri.parse(link)
            val host = uri.host
            val port = uri.port
            if (!host.isNullOrBlank() && port != -1) {
                return Pair(host, port)
            }
            val atIndex = link.indexOf('@')
            val questionIndex = link.indexOf('?')
            val hashIndex = link.indexOf('#')
            val endIndex = listOf(questionIndex, hashIndex, link.length).filter { it != -1 }.minOrNull() ?: link.length
            if (atIndex != -1 && atIndex < endIndex) {
                val hostPortStr = link.substring(atIndex + 1, endIndex)
                val parts = hostPortStr.split(':')
                if (parts.size >= 2) {
                    val host = parts[0]
                    val port = parts[1].toIntOrNull() ?: 443
                    return Pair(host, port)
                }
            }
        } catch (e: Exception) {}
        return null
    }

    private suspend fun runTcpPing(host: String, port: Int): Long = withContext(Dispatchers.IO) {
        val start = System.currentTimeMillis()
        try {
            val socket = java.net.Socket()
            socket.connect(java.net.InetSocketAddress(host, port), 1500)
            socket.close()
            System.currentTimeMillis() - start
        } catch (e: Exception) {
            -1L
        }
    }

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
                        onError("Не найдено подходящих ссылок (VLESS/VMess/Naive/Hysteria2)")
                    }
                } else {
                    withContext(Dispatchers.Main) {
                        links.forEach { link ->
                            ConfigHistoryManager.saveConfigToHistory(context, link)
                        }
                        loadHistory()
                        onComplete(links.size)
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

    private suspend fun fetchSubscriptionContent(url: String): String = withContext(Dispatchers.IO) {
        val connection = java.net.URL(url).openConnection() as java.net.HttpURLConnection
        connection.connectTimeout = 10000
        connection.readTimeout = 10000
        connection.inputStream.bufferedReader().use { it.readText() }
    }

    private fun parseSubscriptionContent(content: String): List<String> {
        val decoded = try {
            io.github.vyomtunnel.sdk.utils.Base64Utils.decode(content)
        } catch (e: Exception) {
            content
        }

        return decoded.split(Regex("[\r\n]+"))
            .map { it.trim() }
            .filter { it.startsWith("vless://") || it.startsWith("vmess://") || it.startsWith("naive+https://") || it.startsWith("hysteria2://") || it.startsWith("hy2://") }
    }

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
        _externalLoading.value = true
        _externalStatusText.value = "Загрузка списка..."
        viewModelScope.launch {
            try {
                val content = fetchSubscriptionContent("https://raw.githubusercontent.com/igareck/vpn-configs-for-russia/main/BLACK_VLESS_RUS.txt")
                val allLinks = content.split(Regex("[\r\n]+"))
                    .map { it.trim() }
                    .filter { it.startsWith("vless://") || it.startsWith("vmess://") || it.startsWith("naive+https://") || it.startsWith("hysteria2://") || it.startsWith("hy2://") }
                    .distinct()
                
                if (allLinks.isEmpty()) {
                    _externalStatusText.value = "Конфигурации не найдены"
                    _externalConfigs.value = emptyList()
                    _externalLoading.value = false
                    return@launch
                }

                _externalStatusText.value = "Проверка серверов (0/${allLinks.size})..."
                
                val checkedConfigs = mutableListOf<Pair<String, Long>>()
                val completedCount = java.util.concurrent.atomic.AtomicInteger(0)
                
                val dispatcher = Dispatchers.IO
                val semaphore = kotlinx.coroutines.sync.Semaphore(30)
                
                coroutineScope {
                    val jobs = allLinks.map { link ->
                        async(dispatcher) {
                            semaphore.acquire()
                            try {
                                val hostPort = parseHostPort(link)
                                val latency = if (hostPort != null) {
                                    runTcpPing(hostPort.first, hostPort.second)
                                } else {
                                    -1L
                                }
                                val count = completedCount.incrementAndGet()
                                if (count % 5 == 0 || count == allLinks.size) {
                                    _externalStatusText.value = "Проверка серверов ($count/${allLinks.size})..."
                                }
                                if (latency >= 0) {
                                    synchronized(checkedConfigs) {
                                        checkedConfigs.add(Pair(link, latency))
                                    }
                                }
                            } finally {
                                semaphore.release()
                            }
                        }
                    }
                    jobs.awaitAll()
                }
                
                val working = checkedConfigs.sortedBy { it.second }
                _externalConfigs.value = working
                _externalStatusText.value = if (working.isEmpty()) "Нет рабочих серверов" else "Найдено рабочих: ${working.size}"
            } catch (e: Exception) {
                _externalStatusText.value = "Ошибка: ${e.message}"
            } finally {
                _externalLoading.value = false
            }
        }
    }
}

