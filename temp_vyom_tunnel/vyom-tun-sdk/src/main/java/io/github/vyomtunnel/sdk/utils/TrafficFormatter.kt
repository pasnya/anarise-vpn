package io.github.vyomtunnel.sdk.utils

import java.text.DecimalFormat
import java.util.Locale
import kotlin.math.log10
import kotlin.math.pow

object TrafficFormatter {

    private val UNITS = arrayOf("B/s", "KB/s", "MB/s", "GB/s", "TB/s")
    private val DECIMAL_FORMAT = DecimalFormat("#,##0.#")

    /**
     * Formats bytes per second into a human-readable string.
     * Uses Locale.US to ensure decimal points are consistent (dots, not commas).
     */
    fun formatSpeed(bytes: Long): String {
        if (bytes <= 0) return "0 ${UNITS[0]}"

        // Determine the magnitude of the value
        val digitGroups = (log10(bytes.toDouble()) / log10(1024.0)).toInt()

        // Ensure index stays within the bounds of our units array
        val unitIndex = digitGroups.coerceIn(0, UNITS.size - 1)

        // Calculate value based on the chosen unit
        val value = bytes / 1024.0.pow(unitIndex.toDouble())

        return String.format(Locale.US, "%s %s", DECIMAL_FORMAT.format(value), UNITS[unitIndex])
    }
}