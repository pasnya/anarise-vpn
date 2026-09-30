package io.github.vyomtunnel.sdk.models

enum class TrafficProbeStatus {
    VERIFIED, LIMITED, INVALID_CONFIG, MISSING_ENGINE, ENGINE_START_FAILED,
    BUSY, TIMEOUT, UNEXPECTED_RESPONSE, NETWORK_ERROR
}

data class TrafficProbeResult(
    val status: TrafficProbeStatus,
    val latency: Long = -1,
    val verifiedAt: Long = 0
) {
    companion object {
        fun expectedResponse(expectedCode: Int, expectedBody: String, code: Int, body: String) =
            code == expectedCode && body.trim() == expectedBody

        fun evaluateSuccess(targets: Set<String>, delays: List<Long>, finalAttempt: Boolean): TrafficProbeResult? {
            val status = when {
                targets.size >= 2 -> TrafficProbeStatus.VERIFIED
                finalAttempt && targets.size == 1 && delays.size >= 2 -> TrafficProbeStatus.LIMITED
                else -> return null
            }
            return TrafficProbeResult(status, delays.sorted()[delays.size / 2], System.currentTimeMillis())
        }
    }
    val isUsable get() = status == TrafficProbeStatus.VERIFIED || status == TrafficProbeStatus.LIMITED
    val label get() = when (status) {
        TrafficProbeStatus.VERIFIED -> "Трафик подтверждён"
        TrafficProbeStatus.LIMITED -> "Частичная доступность"
        TrafficProbeStatus.INVALID_CONFIG -> "Ошибка конфигурации"
        TrafficProbeStatus.MISSING_ENGINE -> "Не установлен VPN-модуль"
        TrafficProbeStatus.ENGINE_START_FAILED -> "Ошибка запуска VPN-модуля"
        TrafficProbeStatus.BUSY -> "Проверка недоступна при активном VPN"
        TrafficProbeStatus.TIMEOUT -> "Таймаут проверки"
        TrafficProbeStatus.UNEXPECTED_RESPONSE -> "Неверный ответ тестового сайта"
        TrafficProbeStatus.NETWORK_ERROR -> "Трафик не подтверждён"
    }
}
