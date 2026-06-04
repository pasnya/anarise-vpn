package com.example.vlessvpn.ui.screens

import android.app.Activity
import android.widget.Toast
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Settings
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
    var showImportDialog by remember { mutableStateOf(false) }
    var updateInfo by remember { mutableStateOf<Pair<String, String>?>(null) }
    val currentVersion = remember {
        try {
            val pInfo = context.packageManager.getPackageInfo(context.packageName, 0)
            pInfo.versionName ?: "1.1.0"
        } catch (e: Exception) {
            "1.1.0"
        }
    }

    LaunchedEffect(Unit) {
        viewModel.checkForUpdates(currentVersion) { version, url ->
            updateInfo = Pair(version, url)
        }
    }

    LaunchedEffect(selectedTab) {
        if (selectedTab == 1 && externalConfigs.isEmpty() && !externalLoading) {
            viewModel.fetchAndCheckExternalConfigs()
        }
    }

    if (updateInfo != null) {
        AlertDialog(
            onDismissRequest = { updateInfo = null },
            title = { Text("Доступно обновление") },
            text = { Text("Доступна новая версия приложения: v${updateInfo?.first}. Хотите скачать её?") },
            confirmButton = {
                Button(
                    onClick = {
                        val intent = android.content.Intent(
                            android.content.Intent.ACTION_VIEW,
                            android.net.Uri.parse(updateInfo?.second)
                        )
                        context.startActivity(intent)
                        updateInfo = null
                    }
                ) {
                    Text("Скачать")
                }
            },
            dismissButton = {
                TextButton(onClick = { updateInfo = null }) {
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
            Spacer(modifier = Modifier.height(24.dp))

            // Header toolbar with settings gear
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    text = "ANARISE",
                    color = MaterialTheme.colorScheme.primary,
                    fontSize = 30.sp,
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
            Spacer(modifier = Modifier.height(16.dp))

            Text(
                text = when (state) {
                    VyomState.CONNECTED -> "Подключено"
                    VyomState.CONNECTING -> "Подключение..."
                    VyomState.DISCONNECTED -> "Отключено"
                    VyomState.ERROR -> "Ошибка"
                    else -> "Ожидание"
                },
                color = if (isConnected) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onBackground,
                fontSize = 18.sp,
                fontWeight = FontWeight.Medium
            )
        }

        item {
            Spacer(modifier = Modifier.height(24.dp))

            // Action Buttons: Paste from Clipboard & Scan QR
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.spacedBy(12.dp)
            ) {
                // Button 1: Paste from Clipboard
                Button(
                    onClick = {
                        val clipboardText = clipboard.getText()?.text
                        if (!clipboardText.isNullOrBlank()) {
                            val trimmed = clipboardText.trim()
                            if (trimmed.startsWith("vless://") || trimmed.startsWith("naive+https://") || trimmed.startsWith("hysteria2://") || trimmed.startsWith("hy2://")) {
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
                        .height(56.dp),
                    colors = ButtonDefaults.buttonColors(
                        containerColor = MaterialTheme.colorScheme.primaryContainer,
                        contentColor = MaterialTheme.colorScheme.onPrimaryContainer
                    ),
                    shape = RoundedCornerShape(16.dp)
                ) {
                    Icon(
                        painter = painterResource(id = R.drawable.ic_paste),
                        contentDescription = "Вставить из буфера",
                        modifier = Modifier.size(24.dp)
                    )
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
                                    if (trimmed.startsWith("vless://") || trimmed.startsWith("naive+https://") || trimmed.startsWith("hysteria2://") || trimmed.startsWith("hy2://")) {
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
                        .height(56.dp),
                    colors = ButtonDefaults.buttonColors(
                        containerColor = MaterialTheme.colorScheme.primaryContainer,
                        contentColor = MaterialTheme.colorScheme.onPrimaryContainer
                    ),
                    shape = RoundedCornerShape(16.dp)
                ) {
                    Icon(
                        painter = painterResource(id = R.drawable.ic_qr_code),
                        contentDescription = "Сканировать QR-код",
                        modifier = Modifier.size(24.dp)
                    )
                }
            }
        }

        item {
            Spacer(modifier = Modifier.height(16.dp))

            // Active Server Card
            val selectedConfigLabel = if (vlessLink.isNotBlank()) {
                getDisplayLabel(vlessLink)
            } else {
                "Сервер не выбран"
            }

            Card(
                modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(16.dp),
                colors = CardDefaults.cardColors(
                    containerColor = MaterialTheme.colorScheme.surface,
                    contentColor = MaterialTheme.colorScheme.onSurface
                ),
                border = androidx.compose.foundation.BorderStroke(1.dp, MaterialTheme.colorScheme.primary.copy(alpha = 0.3f))
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(16.dp),
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Icon(
                        painter = painterResource(id = R.drawable.ic_vpn_tile),
                        contentDescription = null,
                        tint = MaterialTheme.colorScheme.secondary,
                        modifier = Modifier.size(24.dp)
                    )
                    Spacer(modifier = Modifier.width(12.dp))
                    Column(modifier = Modifier.weight(1f)) {
                        Text(
                            text = "Активный сервер",
                            fontSize = 12.sp,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                        Text(
                            text = selectedConfigLabel,
                            fontSize = 15.sp,
                            fontWeight = FontWeight.Bold,
                            color = MaterialTheme.colorScheme.onSurface,
                            maxLines = 1,
                            overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis
                        )
                    }
                }
            }
        }

        item {
            Spacer(modifier = Modifier.height(20.dp))

            Button(
                onClick = {
                    if (isConnected) {
                        viewModel.disconnect()
                    } else {
                        activity?.let { viewModel.connect(it) }
                    }
                },
                modifier = Modifier
                    .fillMaxWidth()
                    .height(56.dp),
                colors = ButtonDefaults.buttonColors(
                    containerColor = if (isConnected) MaterialTheme.colorScheme.surfaceVariant else MaterialTheme.colorScheme.primary,
                    contentColor = if (isConnected) MaterialTheme.colorScheme.onSurface else MaterialTheme.colorScheme.onPrimary
                ),
                shape = RoundedCornerShape(16.dp)
            ) {
                Text(
                    text = if (isConnected) "Отключиться" else "Подключиться",
                    fontSize = 18.sp,
                    fontWeight = FontWeight.Bold
                )
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

            if (historyList.isEmpty()) {
                item {
                    Box(
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(150.dp),
                        contentAlignment = Alignment.Center
                    ) {
                        Text(
                            text = "Список серверов пуст",
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                            fontSize = 14.sp
                        )
                    }
                }
            } else {
                items(historyList) { item ->
                    val ping = pingResults[item]
                    val pingLoad = pingLoading[item] == true
                    ServerCard(
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
                        onPingCheck = { viewModel.checkServerPing(item) }
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
                Spacer(modifier = Modifier.height(8.dp))
            }

            if (externalLoading || externalStatusText.startsWith("Загрузка") || externalStatusText.startsWith("Проверка")) {
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
                        onPingCheck = { viewModel.checkServerPing(item) }
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
