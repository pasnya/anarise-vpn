package com.example.vlessvpn

import androidx.activity.compose.BackHandler
import androidx.compose.runtime.*
import com.example.vlessvpn.ui.screens.DashboardScreen
import com.example.vlessvpn.ui.screens.SettingsScreen
import com.example.vlessvpn.ui.screens.SplitTunnelingScreen

enum class AppScreen {
    MAIN,
    SETTINGS,
    SPLIT_TUNNELING
}

@Composable
fun MainScreen(viewModel: MainViewModel) {
    var currentScreen by remember { mutableStateOf(AppScreen.MAIN) }

    BackHandler(enabled = currentScreen != AppScreen.MAIN) {
        currentScreen = when (currentScreen) {
            AppScreen.SPLIT_TUNNELING -> AppScreen.SETTINGS
            AppScreen.SETTINGS -> AppScreen.MAIN
            AppScreen.MAIN -> AppScreen.MAIN
        }
    }

    when (currentScreen) {
        AppScreen.MAIN -> DashboardScreen(
            viewModel = viewModel,
            onNavigateToSettings = { currentScreen = AppScreen.SETTINGS }
        )
        AppScreen.SETTINGS -> SettingsScreen(
            viewModel = viewModel,
            onNavigateToSplitTunneling = { currentScreen = AppScreen.SPLIT_TUNNELING },
            onBack = { currentScreen = AppScreen.MAIN }
        )
        AppScreen.SPLIT_TUNNELING -> SplitTunnelingScreen(
            viewModel = viewModel,
            onBack = { currentScreen = AppScreen.SETTINGS }
        )
    }
}
