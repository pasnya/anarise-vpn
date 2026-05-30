package io.github.vyomtunnel.sdk.models

import android.util.Log
import java.net.InetSocketAddress
import java.net.Socket
import kotlin.math.abs

object ConnectionProfiler {

    private const val TAG = "VyomProfiler"
    private const val DEFAULT_HOST = "1.1.1.1"
    private const val DEFAULT_PORT = 80
    private const val PROBE_COUNT = 10
    private const val CONNECT_TIMEOUT = 2000
    private const val INTER_PROBE_DELAY = 100L

    /**
     * Runs a sequence of network probes to determine connection quality.
     * Operates on a background thread and returns results via [callback].
     */
    fun runDiagnostics(
        host: String = "1.1.1.1",
        port: Int = 53, // UDP DNS
        count: Int = 10,
        callback: (VyomProfile) -> Unit
    ) {
        kotlin.concurrent.thread(name = "VyomProfiler") {

            var received = 0
            val latencies = mutableListOf<Long>()

            repeat(count) {
                try {
                    val socket = java.net.DatagramSocket()
                    socket.soTimeout = 2000

                    val data = byteArrayOf(0x01)
                    val packet = java.net.DatagramPacket(
                        data,
                        data.size,
                        java.net.InetAddress.getByName(host),
                        port
                    )

                    val start = System.currentTimeMillis()
                    socket.send(packet)

                    val buffer = ByteArray(512)
                    val response = java.net.DatagramPacket(buffer, buffer.size)
                    socket.receive(response)

                    latencies.add(System.currentTimeMillis() - start)
                    received++
                    socket.close()

                } catch (_: Exception) {
                    // timeout = lost packet
                }

                Thread.sleep(100)
            }

            val lossPercent =
                ((count - received).toDouble() / count.toDouble()) * 100.0

            val avgLatency =
                if (latencies.isNotEmpty()) latencies.average().toLong() else 0

            val jitter = if (latencies.size > 1) {
                latencies.zipWithNext { a, b -> kotlin.math.abs(a - b) }.average().toLong()
            } else 0

            val quality = calculateScore(avgLatency, jitter, lossPercent)

            callback(
                VyomProfile(
                    avgLatency = avgLatency,
                    jitter = jitter,
                    packetLoss = lossPercent,
                    qualityScore = quality
                )
            )
        }
    }


    /**
     * Internal algorithm to convert raw metrics into a user-friendly 0-100 score.
     */
    private fun calculateScore(latency: Long, jitter: Long, loss: Double): Int {
        val lossPenalty = loss * 6.0          // High penalty for any packet loss
        val jitterPenalty = jitter / 1.5      // Moderate penalty for instability
        val latencyPenalty = latency / 25.0   // Lower penalty for distance (latency)

        val rawScore = 100.0 - (lossPenalty + jitterPenalty + latencyPenalty)
        return rawScore.toInt().coerceIn(0, 100)
    }
}