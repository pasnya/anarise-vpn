package io.github.vyomtunnel.sdk.models

import org.junit.Assert.*
import org.junit.Test

class TrafficProbeResultTest {
    @Test fun rejectsRedirectAndCaptivePage() {
        assertFalse(TrafficProbeResult.expectedResponse(204, "", 302, ""))
        assertFalse(TrafficProbeResult.expectedResponse(204, "", 200, "login"))
        assertFalse(TrafficProbeResult.expectedResponse(200, "success", 200, "<html>blocked</html>"))
        assertTrue(TrafficProbeResult.expectedResponse(204, "", 204, ""))
        assertTrue(TrafficProbeResult.expectedResponse(200, "success", 200, "success\n"))
    }
    @Test fun requiresIndependentTargetsOrStablePartialAccess() {
        assertNull(TrafficProbeResult.evaluateSuccess(emptySet(), emptyList(), true))
        assertNull(TrafficProbeResult.evaluateSuccess(setOf("one"), listOf(20L), true))
        assertNull(TrafficProbeResult.evaluateSuccess(setOf("one"), listOf(20L, 30L), false))
        val partial = TrafficProbeResult.evaluateSuccess(setOf("one"), listOf(20L, 30L), true)!!
        assertEquals(TrafficProbeStatus.LIMITED, partial.status)
        assertTrue(partial.isUsable)
        assertEquals(TrafficProbeStatus.VERIFIED, TrafficProbeResult.evaluateSuccess(setOf("one", "two"), listOf(20L, 30L), false)!!.status)
        assertFalse(TrafficProbeResult(TrafficProbeStatus.MISSING_ENGINE).isUsable)
        assertFalse(TrafficProbeResult(TrafficProbeStatus.ENGINE_START_FAILED).isUsable)
    }
}
