package io.github.vyomtunnel.sdk.models

data class VyomProfile(
    val avgLatency: Long,
    val jitter: Long,
    val packetLoss: Double,
    val qualityScore: Int
)