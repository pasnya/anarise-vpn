package com.example.vlessvpn.ui.screens

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.automirrored.filled.ArrowForward
import androidx.compose.material3.*
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.example.vlessvpn.MainViewModel
import com.example.vlessvpn.ui.components.SettingSwitchCard

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(
    viewModel: MainViewModel,
    onNavigateToSplitTunneling: () -> Unit,
    onBack: () -> Unit
) {
    val killSwitch by viewModel.killSwitchEnabled.collectAsState()
    val autoStart by viewModel.autoStartEnabled.collectAsState()
    val autoReconnect by viewModel.autoReconnectEnabled.collectAsState()

    Scaffold(
        topBar = {
            TopAppBar(
                title = { Text("Настройки", fontWeight = FontWeight.Bold) },
                navigationIcon = {
                    IconButton(onClick = onBack) {
                        Icon(imageVector = Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Назад")
                    }
                },
                colors = TopAppBarDefaults.topAppBarColors(
                    containerColor = MaterialTheme.colorScheme.background
                )
            )
        }
    ) { paddingValues ->
        Column(
            modifier = Modifier
                .fillMaxSize()
                .background(MaterialTheme.colorScheme.background)
                .padding(paddingValues)
                .padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(16.dp)
        ) {
            SettingSwitchCard(
                title = "Kill Switch",
                description = "Блокировать интернет при обрыве VPN соединения",
                checked = killSwitch,
                onCheckedChange = { viewModel.toggleKillSwitch(it) }
            )

            SettingSwitchCard(
                title = "Автостарт при загрузке",
                description = "Запускать VPN автоматически после включения устройства",
                checked = autoStart,
                onCheckedChange = { viewModel.toggleAutoStart(it) }
            )

            SettingSwitchCard(
                title = "Авто-переподключение",
                description = "Восстанавливать VPN при переключении типа сети (Wi-Fi / мобильные данные)",
                checked = autoReconnect,
                onCheckedChange = { viewModel.toggleAutoReconnect(it) }
            )

            Spacer(modifier = Modifier.height(8.dp))

            Card(
                onClick = onNavigateToSplitTunneling,
                modifier = Modifier.fillMaxWidth(),
                shape = RoundedCornerShape(12.dp),
                colors = CardDefaults.cardColors(
                    containerColor = MaterialTheme.colorScheme.surfaceVariant.copy(alpha = 0.2f)
                )
            ) {
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(16.dp),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically
                ) {
                    Column {
                        Text(
                            text = "Раздельное туннелирование",
                            fontWeight = FontWeight.Bold,
                            fontSize = 16.sp,
                            color = MaterialTheme.colorScheme.onSurface
                        )
                        Spacer(modifier = Modifier.height(4.dp))
                        Text(
                            text = "Исключить приложения из VPN туннеля",
                            fontSize = 12.sp,
                            color = MaterialTheme.colorScheme.onSurfaceVariant
                        )
                    }
                    Icon(
                        imageVector = Icons.AutoMirrored.Filled.ArrowForward,
                        contentDescription = "Перейти"
                    )
                }
            }
        }
    }
}
