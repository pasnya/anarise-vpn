package io.github.vyomtunnel.sdk.utils

import android.content.Context
import android.util.Log
import java.io.File
import java.io.FileOutputStream

/**
 * Manages the Hysteria2 binary process lifecycle.
 *
 * Hysteria2 runs as a standalone executable (ELF binary from official GitHub releases)
 * and acts as a SOCKS5 proxy on 127.0.0.1:20808, the same port used by Xray-core.
 * This allows the existing hev-socks5-tunnel (TUN → SOCKS5) integration to work unchanged.
 */
object HysteriaEngine {

    private const val TAG = "HysteriaEngine"
    private const val BINARY_NAME = "libhysteria2.so"
    private const val CONFIG_NAME = "hysteria2.yaml"
    private const val SOCKS_PORT = 20808

    private var process: Process? = null
    @Volatile
    private var isRunning = false

    /**
     * Starts Hysteria2 client with the given configuration parameters.
     *
     * @param context Android context for file operations
     * @param server server address in host:port format
     * @param auth authentication password
     * @param sni TLS server name indication
     * @param insecure whether to skip TLS certificate verification
     * @param obfsType obfuscation type (e.g. "salamander"), empty for none
     * @param obfsPassword obfuscation password
     * @param logCallback optional callback for log lines from the process
     */
    fun start(
        context: Context,
        server: String,
        auth: String,
        sni: String = "",
        insecure: Boolean = false,
        obfsType: String = "",
        obfsPassword: String = "",
        logCallback: ((String) -> Unit)? = null
    ) {
        stop()

        val filesDir = context.filesDir
        val binaryFile = getBinaryPath(context)

        if (!binaryFile.exists()) {
            throw IllegalStateException("Hysteria2 binary not found at ${binaryFile.absolutePath}")
        }

        // Ensure binary is executable
        binaryFile.setExecutable(true, false)

        // Generate YAML config
        val configFile = File(filesDir, CONFIG_NAME)
        val configContent = buildConfig(server, auth, sni, insecure, obfsType, obfsPassword)
        configFile.writeText(configContent)

        Log.i(TAG, "Starting Hysteria2: server=$server, obfs=$obfsType")

        val processBuilder = ProcessBuilder(
            binaryFile.absolutePath,
            "client",
            "--config", configFile.absolutePath
        )
        processBuilder.directory(filesDir)
        processBuilder.redirectErrorStream(true)

        process = processBuilder.start()
        isRunning = true

        // Read stdout/stderr in a background thread
        Thread({
            try {
                process?.inputStream?.bufferedReader()?.use { reader ->
                    reader.forEachLine { line ->
                        Log.d(TAG, line)
                        logCallback?.invoke("[Hysteria2] $line")
                    }
                }
            } catch (e: Exception) {
                Log.e(TAG, "Log reader error", e)
            }
        }, "HysteriaLogReader").start()

        Log.i(TAG, "Hysteria2 process started, PID=${getProcessPid()}")
    }

    /**
     * Stops the running Hysteria2 process.
     */
    fun stop() {
        try {
            process?.let { proc ->
                proc.destroy()
                try {
                    proc.waitFor()
                } catch (_: InterruptedException) {}
            }
        } catch (e: Exception) {
            Log.e(TAG, "Error stopping Hysteria2", e)
        } finally {
            process = null
            isRunning = false
            Log.i(TAG, "Hysteria2 stopped")
        }
    }

    fun isRunning(): Boolean = isRunning && process != null

    /**
     * Returns path to the Hysteria2 binary.
     * The binary is shipped as libhysteria2.so inside jniLibs and gets
     * extracted by the Android runtime into the native library directory.
     */
    private fun getBinaryPath(context: Context): File {
        val nativeLibDir = context.applicationInfo.nativeLibraryDir
        return File(nativeLibDir, BINARY_NAME)
    }

    /**
     * Builds Hysteria2 YAML client configuration.
     */
    internal fun buildConfig(
        server: String,
        auth: String,
        sni: String,
        insecure: Boolean,
        obfsType: String,
        obfsPassword: String
    ): String {
        val sb = StringBuilder()

        sb.appendLine("server: $server")
        sb.appendLine()
        sb.appendLine("auth: $auth")
        sb.appendLine()

        // TLS settings
        sb.appendLine("tls:")
        if (sni.isNotEmpty()) {
            sb.appendLine("  sni: $sni")
        }
        if (insecure) {
            sb.appendLine("  insecure: true")
        }
        sb.appendLine()

        // Obfuscation (Salamander)
        if (obfsType.isNotEmpty() && obfsPassword.isNotEmpty()) {
            sb.appendLine("obfs:")
            sb.appendLine("  type: $obfsType")
            sb.appendLine("  $obfsType:")
            sb.appendLine("    password: $obfsPassword")
            sb.appendLine()
        }

        // SOCKS5 proxy — same port as Xray uses
        sb.appendLine("socks5:")
        sb.appendLine("  listen: 127.0.0.1:$SOCKS_PORT")

        return sb.toString()
    }

    private fun getProcessPid(): String {
        return try {
            val proc = process ?: return "unknown"
            // Android API 26+ has pid()
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
