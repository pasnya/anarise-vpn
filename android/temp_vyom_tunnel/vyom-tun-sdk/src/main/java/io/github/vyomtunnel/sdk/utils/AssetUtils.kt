package io.github.vyomtunnel.sdk.utils

import android.content.Context
import java.io.File

object AssetUtils {
    fun copyAssets(context: Context) {
        val assets = listOf("geoip.dat", "geosite.dat")
        assets.forEach { fileName ->
            val destFile = File(context.filesDir, fileName)
            if (!destFile.exists()) {
                context.assets.open(fileName).use { input ->
                    destFile.outputStream().use { output ->
                        input.copyTo(output)
                    }
                }
            }
        }
    }
}