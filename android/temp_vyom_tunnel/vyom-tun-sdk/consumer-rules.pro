# 1. Keep the Native Engine (JNI Linkage)
# This prevents renaming the class or the native methods
-keep class io.github.vyomtunnel.core.NativeEngine {
    native <methods>;
    <methods>;
    *;
}

# 2. Keep the TProxyService (Hardcoded in Hev-Tun C code)
# The C code specifically searches for "hev/htproxy/TProxyService"
-keep class hev.htproxy.TProxyService {
    native <methods>;
    *;
}

# 3. Keep SDK Models and Enums
# These are used for broadcasts and callbacks
-keep class io.github.vyomtunnel.sdk.VyomState { *; }
-keep class io.github.vyomtunnel.sdk.models.** { *; }
-keep class io.github.vyomtunnel.sdk.VyomVpnManager$VyomListener { *; }
-keep class io.github.vyomtunnel.sdk.VyomVpnManager$VyomNotificationConfig { *; }

# 4. Keep JSON and Asset Utils
# Reflection might be used during JSON parsing
-keep class io.github.vyomtunnel.sdk.utils.** { *; }

# 5. General JNI protection
-keepclasseswithmembernames class * {
    native <methods>;
}

# 6. Suppress Xray/Go internal warnings
-dontwarn com.xtls.**

