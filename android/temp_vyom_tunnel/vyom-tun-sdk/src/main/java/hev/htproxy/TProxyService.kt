package hev.htproxy

import android.net.VpnService

/**
 * This class must exist because the hev-socks5-tunnel native library
 * looks for it during initialization (JNI_OnLoad).
 */
open class TProxyService : VpnService() {
    external fun TProxyStartService(configPath: String, fd: Int)
    external fun TProxyStopService()
    external fun TProxyGetStats(): LongArray
}