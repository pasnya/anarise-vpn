package com.example.vlessvpn.ui.screens

import android.app.Activity
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.provider.Settings
import android.widget.Toast
import androidx.compose.foundation.background
import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.FileProvider
import com.example.vlessvpn.MainViewModel
import com.example.vlessvpn.R
import com.example.vlessvpn.ui.components.ExitIpCard
import com.example.vlessvpn.ui.components.ServerCard
import com.example.vlessvpn.ui.components.StatCard
import com.example.vlessvpn.utils.FormatterUtils.formatBytes
import com.example.vlessvpn.utils.FormatterUtils.formatDuration
import com.example.vlessvpn.utils.FormatterUtils.formatSpeed
import com.example.vlessvpn.utils.FormatterUtils.getDisplayLabel
import com.google.mlkit.vision.codescanner.GmsBarcodeScanning
import io.github.vyomtunnel.sdk.VyomState
import io.github.vyomtunnel.sdk.VyomVpnManager

private fun String.containsAny(vararg values: String): Boolean = values.any { contains(it) }

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun DashboardScreen(
    viewModel: MainViewModel,
    onNavigateToSettings: () -> Unit
) {
    val state by viewModel.vpnState.collectAsState()
    val vlessLink by viewModel.vlessLink.collectAsState()
    val upSpeed by viewModel.uploadSpeed.collectAsState()
    val downSpeed by viewModel.downloadSpeed.collectAsState()
    val logs by viewModel.logs.collectAsState()
    val historyList by viewModel.configHistory.collectAsState()

    val totalUp by viewModel.totalUploadBytes.collectAsState()
    val totalDown by viewModel.totalDownloadBytes.collectAsState()
    val duration by viewModel.connectionDuration.collectAsState()
    val exitIpInfo by viewModel.exitIpInfo.collectAsState()
    val pingResults by viewModel.pingResults.collectAsState()
    val verificationStatuses by viewModel.verificationStatuses.collectAsState()
    val pingLoading by viewModel.pingLoading.collectAsState()
    val isImporting by viewModel.isImporting.collectAsState()

    val externalConfigs by viewModel.externalConfigs.collectAsState()
    val externalLoading by viewModel.externalLoading.collectAsState()
    val externalStatusText by viewModel.externalStatusText.collectAsState()

    @Suppress("DEPRECATION")
    val clipboard = LocalClipboardManager.current
    val context = LocalContext.current
    val activity = context as? Activity

    val isConnected = state == VyomState.CONNECTED
    var selectedTab by remember { mutableStateOf(0) }
    var serverSearch by remember { mutableStateOf("") }
    var serverRegion by remember { mutableStateOf("all") }
    val filteredHistory = remember(historyList, serverSearch, serverRegion) {
        historyList.filter { link ->
            val value = "${getDisplayLabel(link)} $link".lowercase()
            val searchMatches = serverSearch.isBlank() || value.contains(serverSearch.lowercase())
            val regionMatches = when (serverRegion) {
                "europe" -> !value.containsAny("tokyo", "singapore", "seoul", "japan", "asia", "india", "hong kong", "new york", "los angeles", "chicago", "usa", "canada")
                "asia" -> value.containsAny("tokyo", "singapore", "seoul", "japan", "asia", "india", "hong kong")
                "america" -> value.containsAny("new york", "los angeles", "chicago", "usa", "canada", "america", "miami")
                else -> true
            }
            searchMatches && regionMatches
        }
    }

    var showImportDialog by remember { mutableStateOf(false) }
    var updateInfo by remember { mutableStateOf<com.example.vlessvpn.AppUpdateOffer?>(null) }
    val updateDownload by viewModel.updateDownloadState.collectAsState()
    val currentVersion = remember {
        try {
            val pInfo = context.packageManager.getPackageInfo(context.packageName, 0)
            pInfo.versionName ?: "1.4.23"
        } catch (e: Exception) {
            "1.1.0"
        }
    }

    LaunchedEffect(Unit) {
        viewModel.checkForUpdates(currentVersion) { offer ->
            updateInfo = offer
        }
    }

    LaunchedEffect(selectedTab) {
        if (selectedTab == 1) {
            while (true) {
                viewModel.expireExternalResults()
                if (viewModel.shouldRefreshExternal()) viewModel.fetchAndCheckExternalConfigs()
                kotlinx.coroutines.delay(60_000)
            }
        }
    }

    if (updateInfo != null) {
        val pendingUpdate = updateInfo!!
        AlertDialog(
            onDismissRequest = {
                if (!updateDownload.isDownloading) {
                    viewModel.dismissUpdateVersion(pendingUpdate.version)
                    viewModel.resetUpdateDownloadState()
                    updateInfo = null
                }
            },
            title = { Text("Доступно обновление") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                    Text(
                        updateDownload.error
                            ?: if (updateDownload.isDownloading) updateDownload.message
                            else "Доступна новая версия приложения: v${pendingUpdate.version}. Скачайте и установите её прямо здесь."
                    )
                    if (updateDownload.isDownloading) {
                        LinearProgressIndicator(progress = { updateDownload.progress / 100f }, modifier = Modifier.fillMaxWidth())
                    }
                }
            },
            confirmButton = {
                Button(
                    enabled = !updateDownload.isDownloading,
                    onClick = {
                        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O && !context.packageManager.canRequestPackageInstalls()) {
                            try {
                                context.startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${context.packageName}")))
                                Toast.makeText(context, "Разрешите ANARISE устанавливать приложения, затем вернитесь и нажмите кнопку ещё раз", Toast.LENGTH_LONG).show()
                            } catch (e: Exception) {
                                Toast.makeText(context, "Разрешите установку приложений из ANARISE в настройках Android", Toast.LENGTH_LONG).show()
                            }
                            return@Button
                        }
                        viewModel.downloadUpdate(pendingUpdate) { apkFile ->
                            try {
                                val apkUri = FileProvider.getUriForFile(context, "${context.packageName}.updates", apkFile)
                                val installIntent = Intent(Intent.ACTION_INSTALL_PACKAGE).apply {
                                    data = apkUri
                                    addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                                    addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                                }
                                context.startActivity(installIntent)
                                viewModel.resetUpdateDownloadState()
                            } catch (e: Exception) {
                                Toast.makeText(context, "Не удалось открыть установщик Android: ${e.message}", Toast.LENGTH_LONG).show()
                            }
                        }
                    }
                ) {
                    Text(if (updateDownload.error != null) "Повторить" else if (updateDownload.isDownloading) "Загрузка…" else "Скачать и установить")
                }
            },
            dismissButton = {
                TextButton(onClick = {
                    if (!updateDownload.isDownloading) {
                        viewModel.dismissUpdateVersion(pendingUpdate.version)
                        viewModel.resetUpdateDownloadState()
                        updateInfo = null
                    }
                }) {
                    Text("Позже")
                }
            }
        )
    }

    if (showImportDialog) {
        var subUrl by remember { mutableStateOf("") }
        AlertDialog(
            onDismissRequest = { if (!isImporting) showImportDialog = false },
            title = { Text("Импорт подписки") },
            text = {
                Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                    Text(
                        text = "Введите URL подписки с конфигурациями (VLESS / Naive / Hysteria2):",
                        fontSize = 14.sp,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                    OutlinedTextField(
                        value = subUrl,
                        onValueChange = { subUrl = it },
                        label = { Text("URL подписки") },
                        modifier = Modifier.fillMaxWidth(),
                        singleLine = true,
                        maxLines = 1,
                        enabled = !isImporting
                    )
                    if (isImporting) {
                        Spacer(modifier = Modifier.height(8.dp))
                        Row(
                            horizontalArrangement = Arrangement.spacedBy(12.dp),
                            verticalAlignment = Alignment.CenterVertically
                        ) {
                            CircularProgressIndicator(modifier = Modifier.size(24.dp))
                            Text("Идет импорт серверов...", fontSize = 14.sp)
                        }
                    }
                }
            },
            confirmButton = {
                Button(
                    onClick = {
                        viewModel.importSubscription(
                            url = subUrl,
                            context = context,
                            onComplete = { count ->
                                Toast.makeText(context, "Импортировано $count серверов", Toast.LENGTH_LONG).show()
                                showImportDialog = false
                            },
                            onError = { err ->
                                Toast.makeText(context, err, Toast.LENGTH_LONG).show()
                            }
                        )
                    },
                    enabled = subUrl.isNotBlank() && !isImporting
                ) {
                    Text("Импорт")
                }
            },
            dismissButton = {
                TextButton(
                    onClick = { showImportDialog = false },
                    enabled = !isImporting
                ) {
                    Text("Отмена")
                }
            }
        )
    }

    LazyColumn(
        modifier = Modifier
            .fillMaxSize()
            .background(MaterialTheme.colorScheme.background)
            .padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        item {
            Spacer(modifier = Modifier.height(12.dp))

            // Header toolbar with settings gear
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = "ANARISE",
                    color = MaterialTheme.colorScheme.primary,
                    fontSize = 24.sp,
                    fontWeight = FontWeight.Bold
                )
                IconButton(onClick = onNavigateToSettings) {
                    Icon(
                        imageVector = Icons.Default.Settings,
                        contentDescription = "Настройки",
                        tint = MaterialTheme.colorScheme.secondary,
                        modifier = Modifier.size(28.dp)
                    )
                }
            }
        }

        item {
            Spacer(modifier = Modifier.height(14.dp))

            // Zen status badge & title
            Surface(
                shape = RoundedCornerShape(50),
                color = MaterialTheme.colorScheme.surface,
                border = BorderStroke(1.dp, MaterialTheme.colorScheme.primary.copy(alpha = 0.2f)),
                modifier = Modifier.padding(horizontal = 16.dp)
            ) {
                Row(
                    modifier = Modifier.padding(horizontal = 14.dp, vertical = 6.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Box(
                        modifier = Modifier
                            .size(8.dp)
                            .background(
                                color = when (state) {
                                    VyomState.CONNECTED -> MaterialTheme.colorScheme.primary
                                    VyomState.CONNECTING -> Color(0xFFD9822B)
                                    VyomState.ERROR -> Color(0xFFB84B4B)
                                    else -> Color(0xFF8B9992)
                                },
                                shape = CircleShape
                            )
                    )
                    Spacer(modifier = Modifier.width(8.dp))
                    Text(
                        text = when (state) {
                            VyomState.CONNECTED -> "Подключено"
                            VyomState.CONNECTING -> "Подключение..."
                            VyomState.ERROR -> "Ошибка"
                            else -> "Отключено"
                        },
                        fontSize = 12.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = MaterialTheme.colorScheme.onSurfaceVariant
                    )
                }
            }

            Spacer(modifier = Modifier.height(10.dp))

            Text(
                text = when (state) {
                    VyomState.CONNECTED -> "Вы защищены"
                    VyomState.CONNECTING -> "Подключаемся..."
                    VyomState.ERROR -> "Ошибка подключения"
                    else -> "Защита выключена"
                },
                fontSize = 24.sp,
                fontWeight = FontWeight.Bold,
                color = MaterialTheme.colorScheme.onBackground
            )

            Spacer(modifier = Modifier.height(4.dp))

            val selectedConfigLabel = if (vlessLink.isNotBlank()) {
                getDisplayLabel(vlessLink)
            } else {
                "Сервер не выбран"
            }

            Text(
                text = when (state) {
                    VyomState.CONNECTED -> selectedConfigLabel
                    VyomState.CONNECTING -> "Шифрование канала связи"
                    VyomState.ERROR -> "Попробуйте другой сервер"
                    else -> "Интернет работает напрямую"
                },
                fontSize = 13.sp,
                color = MaterialTheme.colorScheme.onSurfaceVariant
            )
        }

        item {
            Spacer(modifier = Modifier.height(24.dp))

            // Android uses the OS VPNService TUN route; system proxy is not an Android app mode.
            Surface(
                modifier = Modifier.fillMaxWidth().padding(horizontal = 4.dp),
                shape = RoundedCornerShape(22.dp),
                color = MaterialTheme.colorScheme.surface.copy(alpha = 0.68f),
                border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.32f))
            ) {
                Column(modifier = Modifier.padding(horizontal = 16.dp, vertical = 14.dp)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Column(modifier = Modifier.weight(1f)) {
                            Text(
                                text = "Режим подключения",
                                fontSize = 13.sp,
                                fontWeight = FontWeight.SemiBold,
                                color = MaterialTheme.colorScheme.onSurface
                            )
                            Text(
                                text = "На Android доступен системный VPN-туннель",
                                fontSize = 11.sp,
                                color = MaterialTheme.colorScheme.onSurfaceVariant
                            )
                        }
                        TooltipBox(
                            positionProvider = TooltipDefaults.rememberPlainTooltipPositionProvider(),
                            tooltip = {
                                PlainTooltip {
                                    Text("TUN создаёт виртуальный сетевой интерфейс и проводит трафик через VPNService Android. Системный прокси меняет настройки прокси устройства и работает только в приложениях, которые их используют; этот режим в Android-версии не поддерживается.")
                                }
                            },
                            state = rememberTooltipState()
                        ) {
                            IconButton(onClick = {}) {
                                Text("i", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.Bold)
                            }
                        }
                    }
                    Spacer(modifier = Modifier.height(12.dp))
                    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        Surface(
                            modifier = Modifier.weight(1f),
                            shape = RoundedCornerShape(14.dp),
                            color = MaterialTheme.colorScheme.primary.copy(alpha = 0.09f),
                            border = BorderStroke(1.dp, MaterialTheme.colorScheme.primary.copy(alpha = 0.34f))
                        ) {
                            Column(modifier = Modifier.padding(horizontal = 12.dp, vertical = 10.dp)) {
                                Text("TUN", fontSize = 12.sp, fontWeight = FontWeight.SemiBold, color = MaterialTheme.colorScheme.primary)
                                Text("Активный режим", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            }
                        }
                        Surface(
                            modifier = Modifier.weight(1f),
                            shape = RoundedCornerShape(14.dp),
                            color = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.28f),
                            border = BorderStroke(1.dp, MaterialTheme.colorScheme.outlineVariant.copy(alpha = 0.22f))
                        ) {
                            Column(modifier = Modifier.padding(horizontal = 12.dp, vertical = 10.dp)) {
                                Text("Системный прокси", fontSize = 12.sp, fontWeight = FontWeight.Medium, color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.68f))
                                Text("Не поддерживается", fontSize = 10.sp, color = MaterialTheme.colorScheme.onSurfaceVariant.copy(alpha = 0.64f))
                            }
                        }
                    }
                }
            }
        }

        item {
            Spacer(modifier = Modifier.height(24.dp))

            // Large Circular Power Button
            val isConnecting = state == VyomState.CONNECTING
            Surface(
                onClick = {
                    if (isConnected) {
                        viewModel.disconnect()
                    } else {
                        activity?.let { viewModel.connect(it) }
                    }
                },
                modifier = Modifier.size(136.dp),
                shape = CircleShape,
                color = if (isConnected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.surface,
                border = BorderStroke(
                    width = 2.dp,
                    color = if (isConnected) MaterialTheme.colorScheme.primary else if (isConnecting) Color(0xFFD9822B) else MaterialTheme.colorScheme.primary.copy(alpha = 0.3f)
                ),
                shadowElevation = if (isConnected) 8.dp else 4.dp
            ) {
                Column(
                    modifier = Modifier.fillMaxSize(),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center
                ) {
                    Icon(
                        painter = painterResource(id = R.drawable.ic_power),
                        contentDescription = "Power",
                        tint = if (isConnected) Color.White else MaterialTheme.colorScheme.primary,
                        modifier = Modifier.size(44.dp)
                    )
                    Spacer(modifier = Modifier.height(6.dp))
                    Text(
                        text = if (isConnected) "Отключить" else if (isConnecting) "Прервать" else "Подключиться",
                        fontSize = 12.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = if (isConnected) Color.White else MaterialTheme.colorScheme.primary
                    )
                }
            }
        }

        item {
            Spacer(modifier = Modifier.height(20.dp))

            val selectedConfigLabel = if (vlessLink.isNotBlank()) {
                getDisplayLabel(vlessLink)
            } else {
                "Сервер не выбран"
            }

            // Active Server Selector Card
            Card(
                onClick = { selectedTab = 0 },
                modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(16.dp),
                colors = CardDefaults.cardColors(
                    containerColor = MaterialTheme.colorScheme.surface,
                    contentColor = MaterialTheme.colorScheme.onSurface
                ),
                border = BorderStroke(1.dp, MaterialTheme.colorScheme.primary.copy(alpha = 0.2f))
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 16.dp, vertical = 14.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Box(
                        modifier = Modifier
                            .size(38.dp)
                            .background(
                                color = MaterialTheme.colorScheme.primary.copy(alpha = 0.08f),
                                shape = RoundedCornerShape(10.dp)
                            ),
                        contentAlignment = Alignment.Center
                    ) {
                        Icon(
                            painter = painterResource(id = R.drawable.ic_vpn_tile),
                            contentDescription = null,
                            tint = MaterialTheme.colorScheme.primary,
                            modifier = Modifier.size(20.dp)
                        )
                    }
                    Spacer(modifier = Modifier.width(12.dp))
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = "Локация подключения",
                            fontSize = 11.sp,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        Text(
                            text = selectedConfigLabel,
                            fontSize = 14.sp,
                            fontWeight = FontWeight.SemiBold,
                            color = MaterialTheme.colorScheme.onSurface,
                            maxLines = 1,
                            overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                        )
                    }
                    Text(
                        text = "Выбрать ›",
                        fontSize = 12.sp,
                        fontWeight = FontWeight.SemiBold,
                        color = MaterialTheme.colorScheme.primary
                    )
                }
            }
        }

        item {
            Spacer(modifier = Modifier.height(14.dp))

            // Action Buttons: Paste from Clipboard & Scan QR
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(10.dp)
            ) {
                // Button 1: Paste from Clipboard
                Button(
                    onClick = {
                        val clipboardText = clipboard.getText()?.text
                        if (!clipboardText.isNullOrBlank()) {
                            val trimmed = clipboardText.trim()
                            if (trimmed.startsWith("vless://") || trimmed.startsWith("vmess://") || trimmed.startsWith("naive+https://") || trimmed.startsWith("hysteria2://") || trimmed.startsWith("hy2://") || trimmed.startsWith("mieru://") || trimmed.startsWith("mierus://")) {
                                val duplicate = com.example.vlessvpn.data.ConfigHistoryManager.findDuplicate(context, trimmed)
                                if (duplicate != null) {
                                    viewModel.selectConfig(duplicate)
                                    Toast.makeText(context, "Конфигурация уже есть, выбрана", Toast.LENGTH_SHORT).show()
                                } else {
                                    viewModel.updateVlessLink(trimmed)
                                    com.example.vlessvpn.data.ConfigHistoryManager.saveConfigToHistory(context, trimmed)
                                    viewModel.loadHistory()
                                    Toast.makeText(context, "Конфигурация добавлена из буфера", Toast.LENGTH_SHORT).show()
                                }
                            } else {
                                Toast.makeText(context, "Неподдерживаемый формат ссылки в буфере", Toast.LENGTH_SHORT).show()
                            }
                        } else {
                            Toast.makeText(context, "Буфер обмена пуст", Toast.LENGTH_SHORT).show()
                        }
                    },
                    modifier = Modifier
                        .weight(1f)
                        .height(46.dp),
                    colors = ButtonDefaults.buttonColors(
                        containerColor = MaterialTheme.colorScheme.surface,
                        contentColor = MaterialTheme.colorScheme.onSurface
                    ),
                    shape = RoundedCornerShape(11.dp),
                    border = BorderStroke(1.dp, MaterialTheme.colorScheme.primary.copy(alpha = 0.2f))
                ) {
                    Icon(
                        painter = painterResource(id = R.drawable.ic_paste),
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.primary,
                        modifier = Modifier.size(18.dp)
                    )
                    Spacer(modifier = Modifier.width(6.dp))
                    Text("Вставить", fontSize = 13.sp)
                }

                // Button 2: Scan QR Code
                val scanner = remember { GmsBarcodeScanning.getClient(context) }
                Button(
                    onClick = {
                        scanner.startScan()
                            .addOnSuccessListener { barcode ->
                                val rawValue = barcode.rawValue
                                if (!rawValue.isNullOrBlank()) {
                                    val trimmed = rawValue.trim()
                                    if (trimmed.startsWith("vless://") || trimmed.startsWith("vmess://") || trimmed.startsWith("naive+https://") || trimmed.startsWith("hysteria2://") || trimmed.startsWith("hy2://") || trimmed.startsWith("mieru://") || trimmed.startsWith("mierus://")) {
                                        val duplicate = com.example.vlessvpn.data.ConfigHistoryManager.findDuplicate(context, trimmed)
                                        if (duplicate != null) {
                                            viewModel.selectConfig(duplicate)
                                            Toast.makeText(context, "Конфигурация уже есть, выбрана", Toast.LENGTH_SHORT).show()
                                        } else {
                                            viewModel.updateVlessLink(trimmed)
                                            com.example.vlessvpn.data.ConfigHistoryManager.saveConfigToHistory(context, trimmed)
                                            viewModel.loadHistory()
                                            Toast.makeText(context, "Конфигурация добавлена через QR-код", Toast.LENGTH_SHORT).show()
                                        }
                                    } else {
                                        Toast.makeText(context, "Неподдерживаемый формат QR-кода", Toast.LENGTH_SHORT).show()
                                    }
                                }
                            }
                            .addOnFailureListener { e ->
                                Toast.makeText(context, "Ошибка сканирования: ${e.message}", Toast.LENGTH_SHORT).show()
                            }
                    },
                    modifier = Modifier
                        .weight(1f)
                        .height(46.dp),
                    colors = ButtonDefaults.buttonColors(
                        containerColor = MaterialTheme.colorScheme.surface,
                        contentColor = MaterialTheme.colorScheme.onSurface
                    ),
                    shape = RoundedCornerShape(11.dp),
                    border = BorderStroke(1.dp, MaterialTheme.colorScheme.primary.copy(alpha = 0.2f))
                ) {
                    Icon(
                        painter = painterResource(id = R.drawable.ic_qr_code),
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.primary,
                        modifier = Modifier.size(18.dp)
                    )
                    Spacer(modifier = Modifier.width(6.dp))
                    Text("QR-код", fontSize = 13.sp)
                }
            }
        }


        if (isConnected && exitIpInfo != null) {
            item {
                Spacer(modifier = Modifier.height(16.dp))
                ExitIpCard(info = exitIpInfo!!)
            }
        }

        item {
            Spacer(modifier = Modifier.height(20.dp))

            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                Column(modifier = Modifier.weight(1f)) {
                    StatCard(title = "Отдача", value = formatSpeed(upSpeed), modifier = Modifier.fillMaxWidth())
                    if (isConnected) {
                        Spacer(modifier = Modifier.height(8.dp))
                        StatCard(title = "Всего отд.", value = formatBytes(totalUp), modifier = Modifier.fillMaxWidth())
                    }
                }
                Column(modifier = Modifier.weight(1f)) {
                    StatCard(title = "Загрузка", value = formatSpeed(downSpeed), modifier = Modifier.fillMaxWidth())
                    if (isConnected) {
                        Spacer(modifier = Modifier.height(8.dp))
                        StatCard(title = "Всего загр.", value = formatBytes(totalDown), modifier = Modifier.fillMaxWidth())
                    }
                }
            }
        }

        if (isConnected) {
            item {
                Spacer(modifier = Modifier.height(12.dp))
                StatCard(title = "Время подключения", value = formatDuration(duration), modifier = Modifier.fillMaxWidth())
            }
        }

        item {
            Spacer(modifier = Modifier.height(24.dp))

            // Tabs container (Servers History, External & Logs)
            PrimaryTabRow(
                selectedTabIndex = selectedTab,
                containerColor = Color.Transparent,
                contentColor = MaterialTheme.colorScheme.primary,
                modifier = Modifier.fillMaxWidth()
            ) {
                Tab(
                    selected = selectedTab == 0,
                    onClick = { selectedTab = 0 },
                    text = { Text("Серверы", fontWeight = FontWeight.Bold, fontSize = 14.sp) }
                )
                Tab(
                    selected = selectedTab == 1,
                    onClick = { selectedTab = 1 },
                    text = { Text("Внешние", fontWeight = FontWeight.Bold, fontSize = 14.sp) }
                )
                Tab(
                    selected = selectedTab == 2,
                    onClick = { selectedTab = 2 },
                    text = { Text("Логи", fontWeight = FontWeight.Bold, fontSize = 14.sp) }
                )
            }

            Spacer(modifier = Modifier.height(12.dp))
        }

        if (selectedTab == 0) {
            item {
                OutlinedTextField(
                    value = serverSearch,
                    onValueChange = { serverSearch = it },
                    modifier = Modifier.fillMaxWidth(),
                    singleLine = true,
                    leadingIcon = { Icon(Icons.Default.Search, contentDescription = null) },
                    placeholder = { Text("Поиск по городам и странам...", fontSize = 12.sp) },
                    shape = RoundedCornerShape(12.dp)
                )
                Spacer(modifier = Modifier.height(8.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp), modifier = Modifier.fillMaxWidth()) {
                    listOf("all" to "Все", "europe" to "Европа", "asia" to "Азия", "america" to "Америка").forEach { (region, label) ->
                        FilterChip(
                            selected = serverRegion == region,
                            onClick = { serverRegion = region },
                            label = { Text(label, fontSize = 11.sp) },
                            modifier = Modifier.weight(1f)
                        )
                    }
                }
                Spacer(modifier = Modifier.height(12.dp))
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        text = "Список серверов",
                        color = MaterialTheme.colorScheme.primary,
                        fontWeight = FontWeight.Bold,
                        fontSize = 16.sp
                    )
                    Row(
                        horizontalArrangement = Arrangement.spacedBy(8.dp),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        IconButton(onClick = { viewModel.checkAllPings() }) {
                            Icon(
                                imageVector = Icons.Default.Refresh,
                                contentDescription = "Проверить все пинги",
                                tint = MaterialTheme.colorScheme.secondary,
                                modifier = Modifier.size(20.dp)
                            )
                        }
                        IconButton(onClick = { showImportDialog = true }) {
                            Icon(
                                imageVector = Icons.Default.Add,
                                contentDescription = "Импорт подписки",
                                tint = MaterialTheme.colorScheme.secondary,
                                modifier = Modifier.size(24.dp)
                            )
                        }
                    }
                }
                Spacer(modifier = Modifier.height(8.dp))
            }

            if (filteredHistory.isEmpty()) {
                item {
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(150.dp),
                        contentAlignment = Alignment.Center
                    ) {
                        Text(
                            text = if (historyList.isEmpty()) "Список серверов пуст" else "По фильтру серверы не найдены",
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            fontSize = 14.sp
                        )
                    }
                }
            } else {
                items(filteredHistory) { item ->
                    val ping = pingResults[item]
                    val pingLoad = pingLoading[item] == true
                    ServerCard(
                        verificationStatus = verificationStatuses[item],
                        displayName = getDisplayLabel(item),
                        link = item,
                        isSelected = item.trim() == vlessLink.trim(),
                        ping = ping,
                        pingLoading = pingLoad,
                        onSelect = { 
                            viewModel.selectConfig(item)
                            activity?.let { viewModel.connect(it) }
                        },
                        onDelete = { viewModel.deleteConfigFromHistory(item) },
                        onPingCheck = { viewModel.checkServerPing(item) },
                        onShare = {
                            val sendIntent = android.content.Intent(android.content.Intent.ACTION_SEND).apply {
                                type = "text/plain"
                                putExtra(android.content.Intent.EXTRA_TEXT, item)
                            }
                            context.startActivity(android.content.Intent.createChooser(sendIntent, "Поделиться конфигурацией"))
                        }
                    )
                    Spacer(modifier = Modifier.height(8.dp))
                }
            }
        } else if (selectedTab == 1) {
            item {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text(
                        text = "Внешние списки",
                        color = MaterialTheme.colorScheme.primary,
                        fontWeight = FontWeight.Bold,
                        fontSize = 16.sp
                    )
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        if (externalLoading) {
                            TextButton(onClick = { viewModel.cancelExternalScan() }) {
                                Text("Стоп", color = MaterialTheme.colorScheme.error, fontSize = 12.sp, fontWeight = FontWeight.Bold)
                            }
                        }
                        IconButton(
                            onClick = { viewModel.fetchAndCheckExternalConfigs() },
                            enabled = !externalLoading
                        ) {
                            Icon(
                                imageVector = Icons.Default.Refresh,
                                contentDescription = "Обновить списки",
                                tint = MaterialTheme.colorScheme.secondary,
                                modifier = Modifier.size(20.dp)
                            )
                        }
                    }
                }
                Spacer(modifier = Modifier.height(8.dp))
            }

            if ((externalLoading || externalStatusText.startsWith("Загрузка") || externalStatusText.startsWith("Проверка")) && externalConfigs.isEmpty()) {
                item {
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(150.dp),
                        verticalArrangement = Arrangement.Center,
                        horizontalAlignment = Alignment.CenterHorizontally
                    ) {
                        CircularProgressIndicator(modifier = Modifier.size(36.dp))
                        Spacer(modifier = Modifier.height(12.dp))
                        Text(
                            text = externalStatusText,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            fontSize = 14.sp
                        )
                    }
                }
            } else if (externalConfigs.isEmpty()) {
                item {
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(150.dp),
                        contentAlignment = Alignment.Center
                    ) {
                        Text(
                            text = externalStatusText.ifBlank { "Список пуст" },
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            fontSize = 14.sp
                        )
                    }
                }
            } else {
                item {
                    Text(
                        text = externalStatusText,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                        fontSize = 12.sp,
                        modifier = Modifier
                            .fillMaxWidth()
                            .padding(bottom = 8.dp)
                    )
                }
                items(externalConfigs) { (item, ping) ->
                    ServerCard(
                        verificationStatus = verificationStatuses[item],
                        displayName = getDisplayLabel(item),
                        link = item,
                        isSelected = item.trim() == vlessLink.trim(),
                        ping = ping,
                        pingLoading = false,
                        onSelect = { 
                            viewModel.selectConfig(item)
                            activity?.let { viewModel.connect(it) }
                        },
                        onDelete = null,
                        onPingCheck = { viewModel.checkServerPing(item) },
                        onShare = {
                            val sendIntent = android.content.Intent(android.content.Intent.ACTION_SEND).apply {
                                type = "text/plain"
                                putExtra(android.content.Intent.EXTRA_TEXT, item)
                            }
                            context.startActivity(android.content.Intent.createChooser(sendIntent, "Поделиться конфигурацией"))
                        }
                    )
                    Spacer(modifier = Modifier.height(8.dp))
                }
            }
        } else {
            item {
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Text("Логи", color = MaterialTheme.colorScheme.primary, fontWeight = FontWeight.Bold)
                    Row {
                        TextButton(onClick = {
                            clipboard.setText(AnnotatedString(logs.joinToString("\n")))
                        }) {
                            Text("Скопировать")
                        }
                        TextButton(onClick = {
                            val shareIntent = android.content.Intent(android.content.Intent.ACTION_SEND).apply {
                                type = "text/plain"
                                putExtra(android.content.Intent.EXTRA_TEXT, logs.joinToString("\n"))
                            }
                            context.startActivity(android.content.Intent.createChooser(shareIntent, "Поделиться логами"))
                        }) {
                            Text("Поделиться")
                        }
                    }
                }
                Spacer(modifier = Modifier.height(8.dp))
            }

            if (logs.isEmpty()) {
                item {
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(150.dp),
                        contentAlignment = Alignment.Center
                    ) {
                        Text(
                            text = "Логи отсутствуют",
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            fontSize = 12.sp
                        )
                    }
                }
            } else {
                items(logs) { log ->
                    Text(
                        text = log,
                        color = MaterialTheme.colorScheme.onSurface,
                        fontSize = 11.sp,
                        fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace,
                        modifier = Modifier
                            .fillMaxWidth()
                            .background(MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.2f))
                            .padding(horizontal = 12.dp, vertical = 2.dp)
                    )
                }
            }
        }
    }
}
