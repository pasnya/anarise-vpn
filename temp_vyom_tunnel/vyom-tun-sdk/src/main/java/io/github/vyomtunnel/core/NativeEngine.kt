package io.github.vyomtunnel.core

import android.net.VpnService
import android.util.Log

internal object NativeEngine {
    var vpnService: VpnService? = null

    init {
        try {
//            System.loadLibrary("xray")
//            System.loadLibrary("hev-socks5-tunnel")
//            System.loadLibrary("vyom-v2ray")
            System.loadLibrary("vyom-v2ray")
            System.loadLibrary("hev-socks5-tunnel")
            System.loadLibrary("xray")
        } catch (e: UnsatisfiedLinkError) {
            e.printStackTrace()
        }
    }

    external fun startXray(config: String, assetPath: String): Int

    external fun stopXray()

    external fun validateConfig(config: String, assetPath: String): String?

    external fun initNative(vpnService: VpnService)

    @JvmStatic
    fun protectSocket(fd: Int): Boolean {
        val result = vpnService?.protect(fd) ?: false
        // CRITICAL DEBUG LOG: If you don't see this in logcat,
        // the native library isn't calling the protection logic.
        Log.d("VyomNative", "Protecting socket FD $fd: Status = $result")
        return result
    }
}