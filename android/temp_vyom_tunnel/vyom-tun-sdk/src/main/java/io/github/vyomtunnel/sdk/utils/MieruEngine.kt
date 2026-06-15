package io.github.vyomtunnel.sdk.utils

import android.content.Context
import android.util.Log
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * Manages the Mieru client binary process lifecycle.
 */
object MieruEngine {

    private const val TAG = "MieruEngine"
    private const val BINARY_NAME = "libmieru.so"
    private const val CONFIG_NAME = "mieru.json"
    private const val SOCKS_PORT = 20808

    private var process: Process? = null
    @Volatile
    private var isRunning = false

    fun start(
        context: Context,
        server: String,
        username: String,
        password: String,
        multiplexing: String = "MULTIPLEXING_LOW",
        transport: String = "TCP",
        logCallback: ((String) -> Unit)? = null
    ) {
        stop()

        val filesDir = context.filesDir
        val binaryFile = getBinaryPath(context)

        if (!binaryFile.exists()) {
            throw IllegalStateException("Mieru binary not found at ${binaryFile.absolutePath}")
        }

        // Ensure binary is executable
        binaryFile.setExecutable(true, false)

        // Generate JSON config
        val configFile = File(filesDir, CONFIG_NAME)
        val configContent = buildConfig(server, username, password, multiplexing, transport)
        configFile.writeText(configContent)

        Log.i(TAG, "Starting Mieru: server=$server, username=$username")

        val processBuilder = ProcessBuilder(
            binaryFile.absolutePath,
            "run"
        )
        processBuilder.directory(filesDir)
        processBuilder.environment()["MIERU_CONFIG_JSON_FILE"] = configFile.absolutePath
        processBuilder.redirectErrorStream(true)

        process = processBuilder.start()
        isRunning = true

        // Read stdout/stderr in a background thread
        Thread({
            try {
                process?.inputStream?.bufferedReader()?.use { reader ->
                    reader.forEachLine { line ->
                        Log.d(TAG, line)
                        logCallback?.invoke("[Mieru] $line")
                    }
                }
            } catch (e: Exception) {
                Log.e(TAG, "Log reader error", e)
            }
        }, "MieruLogReader").start()

        Log.i(TAG, "Mieru process started, PID=${getProcessPid()}")
    }

    fun stop() {
        try {
            process?.let { proc ->
                proc.destroy()
                try {
                    proc.waitFor()
                } catch (_: InterruptedException) {}
            }
        } catch (e: Exception) {
            Log.e(TAG, "Error stopping Mieru", e)
        } finally {
            process = null
            isRunning = false
            Log.i(TAG, "Mieru stopped")
        }
    }

    fun isRunning(): Boolean = isRunning && process != null

    private fun getBinaryPath(context: Context): File {
        val nativeLibDir = context.applicationInfo.nativeLibraryDir
        return File(nativeLibDir, BINARY_NAME)
    }

    internal fun buildConfig(
        server: String,
        username: String,
        password: String,
        multiplexing: String,
        transport: String
    ): String {
        val json = JSONObject()
        val profile = JSONObject().apply {
            put("profileName", "default")
            put("user", JSONObject().apply {
                put("name", username)
                put("password", password)
            })
            put("servers", JSONArray().put(
                JSONObject().apply {
                    put("ipAddress", server)
                    put("domainName", "")
                    put("portBindings", JSONArray().put(
                        JSONObject().apply {
                            put("port", 443)
                            put("protocol", transport)
                        }
                    ))
                }
            ))
            put("mtu", 1350)
            put("multiplexing", JSONObject().apply {
                put("level", multiplexing)
            })
            put("handshakeMode", "HANDSHAKE_STANDARD")
        }

        // Parse host and port from server string
        val host: String
        val port: Int
        if (server.contains(":")) {
            host = server.substringBefore(":")
            port = server.substringAfter(":").toIntOrNull() ?: 443
        } else {
            host = server
            port = 443
        }

        val serverArray = profile.getJSONArray("servers")
        val serverObj = serverArray.getJSONObject(0)
        serverObj.put("ipAddress", host)
        val portBindings = serverObj.getJSONArray("portBindings")
        val portObj = portBindings.getJSONObject(0)
        portObj.put("port", port)

        json.put("profiles", JSONArray().put(profile))
        json.put("activeProfile", "default")
        json.put("rpcPort", 0)
        json.put("socks5Port", SOCKS_PORT)
        json.put("socks5ListenLAN", false)
        json.put("httpProxyPort", 0)
        json.put("httpProxyListenLAN", false)
        json.put("loggingLevel", "INFO")

        return json.toString(2)
    }

    private fun getProcessPid(): String {
        return try {
            val proc = process ?: return "unknown"
            if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.O) {
                proc.javaClass.getMethod("pid").invoke(proc).toString()
            } else {
                val f = proc.javaClass.getDeclaredField("pid")
                f.isAccessible = true
                f.getInt(proc).toString()
            }
        } catch (e: Exception) {
            "unknown"
        }
    }
}
