package com.example.vlessvpn.ui.screens

import android.content.Context
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.automirrored.filled.ArrowBack
import androidx.compose.material.icons.filled.Search
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import com.example.vlessvpn.MainViewModel
import com.example.vlessvpn.ui.components.AppInfo
import com.example.vlessvpn.ui.components.AppRow
import io.github.vyomtunnel.sdk.VyomVpnManager
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SplitTunnelingScreen(
    viewModel: MainViewModel,
    onBack: () -> Unit
) {
    val context = LocalContext.current
    val packageManager = context.packageManager

    val appsState = produceState<List<AppInfo>>(initialValue = emptyList()) {
        value = withContext(Dispatchers.IO) {
            getInstalledApps(context)
        }
    }

    var excludedApps by remember {
        mutableStateOf(VyomVpnManager.getExcludedApps(context).toSet())
    }

    var searchQuery by remember { mutableStateOf("") }

    val filteredApps = remember(appsState.value, searchQuery) {
        if (searchQuery.isBlank()) {
            appsState.value
        } else {
            appsState.value.filter {
                it.label.contains(searchQuery, ignoreCase = true) ||
                it.packageName.contains(searchQuery, ignoreCase = true)
            }
        }
    }

    Scaffold(
        topBar = {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .background(MaterialTheme.colorScheme.background)
            ) {
                TopAppBar(
                    title = { Text("Раздельное туннелирование", fontWeight = FontWeight.Bold) },
                    navigationIcon = {
                        IconButton(onClick = onBack) {
                            Icon(imageVector = Icons.AutoMirrored.Filled.ArrowBack, contentDescription = "Назад")
                        }
                    },
                    colors = TopAppBarDefaults.topAppBarColors(
                        containerColor = MaterialTheme.colorScheme.background
                    )
                )

                OutlinedTextField(
                    value = searchQuery,
                    onValueChange = { searchQuery = it },
                    placeholder = { Text("Поиск приложений...") },
                    leadingIcon = { Icon(imageVector = Icons.Default.Search, contentDescription = null) },
                    modifier = Modifier
                        .fillMaxWidth()
                        .padding(horizontal = 16.dp, vertical = 8.dp),
                    shape = RoundedCornerShape(12.dp),
                    colors = TextFieldDefaults.colors(
                        focusedContainerColor = Color.Transparent,
                        unfocusedContainerColor = Color.Transparent
                    )
                )
            }
        }
    ) { paddingValues ->
        if (appsState.value.isEmpty()) {
            Box(
                modifier = Modifier
                    .fillMaxSize()
                    .padding(paddingValues),
                contentAlignment = Alignment.Center
            ) {
                CircularProgressIndicator()
            }
        } else {
            LazyColumn(
                modifier = Modifier
                    .fillMaxSize()
                    .background(MaterialTheme.colorScheme.background)
                    .padding(paddingValues)
                    .padding(horizontal = 16.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                items(filteredApps, key = { it.packageName }) { app ->
                    val isExcluded = excludedApps.contains(app.packageName)

                    AppRow(
                        app = app,
                        packageManager = packageManager,
                        isExcluded = isExcluded,
                        onExclusionToggle = {
                            VyomVpnManager.toggleAppExclusion(context, app.packageName)
                            excludedApps = VyomVpnManager.getExcludedApps(context).toSet()
                        }
                    )
                }
            }
        }
    }
}

private fun getInstalledApps(context: Context): List<AppInfo> {
    val pm = context.packageManager
    val apps = pm.getInstalledApplications(android.content.pm.PackageManager.GET_META_DATA)
    val list = mutableListOf<AppInfo>()
    for (app in apps) {
        val launchIntent = pm.getLaunchIntentForPackage(app.packageName)
        if (launchIntent != null || (app.flags and android.content.pm.ApplicationInfo.FLAG_SYSTEM) == 0) {
            val label = app.loadLabel(pm).toString()
            list.add(AppInfo(label, app.packageName))
        }
    }
    return list.sortedBy { it.label.lowercase() }
}
