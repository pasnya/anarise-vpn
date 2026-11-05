package com.example.vlessvpn.theme

import android.app.Activity
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.SideEffect
import androidx.compose.ui.graphics.toArgb
import androidx.compose.ui.platform.LocalView
import androidx.core.view.WindowCompat

private val LightColorScheme = lightColorScheme(
    primary = ElectricNeon,
    secondary = CyanAccent,
    tertiary = ElectricNeon,
    background = Charcoal,
    surface = SlateGray,
    surfaceVariant = androidx.compose.ui.graphics.Color(0xFFEDF2EE),
    primaryContainer = androidx.compose.ui.graphics.Color(0xFFEDF5F1),
    secondaryContainer = androidx.compose.ui.graphics.Color(0xFFEDF5F1),
    onPrimary = androidx.compose.ui.graphics.Color.White,
    onSecondary = androidx.compose.ui.graphics.Color.White,
    onTertiary = androidx.compose.ui.graphics.Color.White,
    onBackground = WhiteText,
    onSurface = WhiteText,
    onSurfaceVariant = GrayText,
)

@Composable
fun VlessVPNTheme(
    darkTheme: Boolean = isSystemInDarkTheme(),
    content: @Composable () -> Unit
) {
    val colorScheme = LightColorScheme
    
    val view = LocalView.current
    if (!view.isInEditMode) {
        SideEffect {
            val window = (view.context as Activity).window
            window.statusBarColor = colorScheme.background.toArgb()
            WindowCompat.getInsetsController(window, view).isAppearanceLightStatusBars = true
        }
    }

    MaterialTheme(
        colorScheme = colorScheme,
        typography = Typography,
        content = content
    )
}
