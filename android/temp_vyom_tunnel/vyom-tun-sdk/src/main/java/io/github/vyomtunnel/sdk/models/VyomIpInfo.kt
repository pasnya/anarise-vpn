package io.github.vyomtunnel.sdk.models

import org.json.JSONObject

data class VyomIpInfo(
    val ip: String,
    val success: Boolean,
    val country: String,
    val countryCode: String,
    val region: String,
    val city: String,
    val latitude: Double,
    val longitude: Double,
    val isp: String,
    val org: String,
    val timezone: String,
    val flagUrl: String
) {
    companion object {
        fun fromJson(json: String): VyomIpInfo {
            val v = JSONObject(json)
            val conn = v.optJSONObject("connection")
            val tz = v.optJSONObject("timezone")
            val flag = v.optJSONObject("flag")

            return VyomIpInfo(
                ip = v.optString("ip", "0.0.0.0"),
                success = v.optBoolean("success", false),
                country = v.optString("country", "Unknown"),
                countryCode = v.optString("country_code", ""),
                region = v.optString("region", ""),
                city = v.optString("city", ""),
                latitude = v.optDouble("latitude", 0.0),
                longitude = v.optDouble("longitude", 0.0),
                isp = conn?.optString("isp", "Unknown") ?: "Unknown",
                org = conn?.optString("org", "Unknown") ?: "Unknown",
                timezone = tz?.optString("id", "UTC") ?: "UTC",
                flagUrl = flag?.optString("img", "") ?: ""
            )
        }
    }
}