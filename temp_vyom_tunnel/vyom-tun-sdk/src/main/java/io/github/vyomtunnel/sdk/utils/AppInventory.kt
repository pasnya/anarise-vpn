package io.github.vyomtunnel.sdk.utils

import android.content.Context
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import android.graphics.drawable.Drawable
import io.github.vyomtunnel.sdk.VyomVpnManager

/**
 * Model representing an installed application.
 */
data class VyomAppInfo(
    val name: String,
    val packageName: String,
    val icon: Drawable,
    val isSystemApp: Boolean,
    val isExcluded: Boolean
)

object AppInventory {

    /**
     * Retrieves a list of all installed apps that possess a launch intent.
     * Maps them to the internal VyomAppInfo model for UI consumption.
     */
    fun getInstalledApps(context: Context): List<VyomAppInfo> {
        val pm = context.packageManager
        val installedApplications = pm.getInstalledApplications(PackageManager.GET_META_DATA)
        val currentExclusions = VyomVpnManager.getExcludedApps(context)

        return installedApplications.asSequence()
            .filter { app ->
                // Only include apps the user can interact with via a launcher
                pm.getLaunchIntentForPackage(app.packageName) != null
            }
            .map { app ->
                val name = app.loadLabel(pm).toString()
                val icon = app.loadIcon(pm)
                val isSystem = (app.flags and ApplicationInfo.FLAG_SYSTEM) != 0
                val isExcluded = currentExclusions.contains(app.packageName)

                VyomAppInfo(
                    name = name,
                    packageName = app.packageName,
                    icon = icon,
                    isSystemApp = isSystem,
                    isExcluded = isExcluded
                )
            }
            .sortedBy { it.name.lowercase() } // Robust alphabetical sorting
            .toList()
    }
}