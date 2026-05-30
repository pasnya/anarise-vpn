[![JitPack](https://jitpack.io/v/VyomOS/vyom-tunnel-android.svg)](https://jitpack.io/#VyomOS/vyom-tunnel-android)
[![Android](https://img.shields.io/badge/Android-API%2024%2B-brightgreen.svg)](https://developer.android.com)
[![License: MIT](https://img.shields.io/badge/License-MIT-blue.svg)](LICENSE)
# Vyom Tunnel Android SDK

Vyom Tunnel is a **high-performance Android VPN SDK** built on top of the **Xray core**.  
It provides a ready-to-use, production-grade VPN engine with split tunneling, kill switch, auto-reconnect, and boot persistence.

This SDK is designed for developers who want **full control**, **native performance**, and **OEM-safe behavior** across Android devices.

## 📚 Contents

- [Features](#-features)
- [Native Compatibility (16 KB Page Size)](#-native-compatibility-16-kb-page-size)
- [ABI & App Size Optimization](#-abi--app-size-optimization)
- [Changelog](#-changelog)
- [Installation](#-installation-jitpack)
- [Quick Start](#-quick-start)
- [Split Tunneling](#-split-tunneling-exclude-apps)
- [Kill Switch](#-kill-switch)
- [Diagnostics](#-diagnostics--utilities)
- [ProGuard / R8](#-proguard--r8)
- [Requirements](#-requirements)
- [Versioning](#-versioning)
- [Contributing](#-contributing)
- [Community](#-community--support)
- [License](#-license)
  
---

## ✨ Features

- 🚀 High-performance native VPN core
- 🔌 Simple Kotlin API
- 🌐 Supports link-based and raw JSON configurations
- 🔁 Auto-reconnect on network change
- 🔐 Optional kill switch support
- 📦 Split tunneling (exclude apps)
- 📡 Traffic statistics (upload / download)
- 🧠 Connection diagnostics (latency, jitter, loss)
- 🧩 Works with modern Android VPN APIs
- 🧱 **16 KB page size aligned native binaries**
- 📱 Android 7.0+ (API 24+) support
- 🏗️ ABI-aware native packaging (ARM / x86)

---

## 🧬 Native Compatibility (16 KB Page Size)

Vyom Tunnel SDK ships with **fully 16 KB page size aligned native libraries**.

### Why this matters

Android is moving toward enforcing **16 KB memory page sizes**, especially on:
- Android 14+
- Newer kernels and OEM devices

This SDK is:
- ✅ **Android 14+ ready**
- ✅ Compatible with large page size devices
- ✅ Free from `dlopen failed` / linker crashes
- ✅ Safe for future Google Play enforcement

> Many native SDKs still ship 4 KB-only binaries. Vyom Tunnel is future-proof by design.

---

## 🧠 ABI & App Size Optimization

Supported ABIs:
- `armeabi-v7a`
- `arm64-v8a`
- `x86_64`

When distributed via **Android App Bundle (AAB)**:
- Only the required ABI is delivered to the device
- Unused native libraries are automatically stripped
- App size remains minimal

No extra configuration is required.

---
## Changelog
See [CHANGELOG.md](CHANGELOG.md) for release history.

---

## 📦 Installation (JitPack)

### Step 1: Add JitPack repository

In `settings.gradle` or `settings.gradle.kts`:

```gradle
dependencyResolutionManagement {
    repositories {
        google()
        mavenCentral()
        maven { url "https://jitpack.io" }
    }
}
```

### Step 2: Add the dependency
```
dependencies {
    implementation "com.github.VyomOS:vyom-tunnel-android:v1.0.1"
}
```

---

## 🚀 Quick Start

### Initialize the SDK
Call this once (e.g. in `Application` or first Activity):
```
VyomVpnManager.initialize(context)
```

### Connect using link or JSON
```
val error = VyomVpnManager.connect(context, configString)
if (error != null) {
    // Handle validation error
}
```
If VPN permission is required: 
```
VyomVpnManager.connectWithPermission(activity, configString)
```

### Stop VPN
```
VyomVpnManager.stop(context)
```

### 🔔 Listen to VPN State & Traffic
```
VyomVpnManager.registerListener(context, object : VyomVpnManager.VyomListener {
    override fun onStateChanged(state: VyomState) {
        // CONNECTED, DISCONNECTED, CONNECTING, etc.
    }

    override fun onTrafficUpdate(up: Long, down: Long) {
        // bytes per second
    }
})

```
Don't forget to unregister 
```
VyomVpnManager.unregisterListener(context)
```

### 🔀 Split Tunneling (Exclude Apps)
```
VyomVpnManager.toggleAppExclusion(context, "com.example.app")
```
Retrieve excluded apps:
```
val excluded = VyomVpnManager.getExcludedApps(context)
```
### 🔐 Kill Switch
```
VyomVpnManager.setKillSwitch(context, true)
val enabled = VyomVpnManager.isKillSwitchEnabled(context)
```

### Auto Start & Auto Reconnect 
```
VyomVpnManager.setAutoStartEnabled(context, true)
VyomVpnManager.setAutoReconnectEnabled(context, true)
```

### 🧪 Diagnostics & Utilities
#### Check Internet
```
VyomVpnManager.checkInternet { isAvailable ->
    // true / false
}
```

#### Performance Profile
```
VyomVpnManager.getPerformanceProfile { profile ->
    // latency, jitter, packet loss
}
```

#### Fetch Exit IP Info
```
VyomVpnManager.fetchIpInfo { info ->
    // ip, country, city, isp
}
```

### 🛡️ ProGuard / R8
The SDK ships with consumer ProGuard rules, so no additional configuration is required by the host app.
Safe for: 
- Minified release builds
- R8 / ProGuard
- Play Store builds

### 🧱 Requirements
- Android API 24+
- Kotlin or Java host app
- VPN permission granted by user

## 🔢 Versioning

Vyom Tunnel SDK follows **Semantic Versioning**:

- **MAJOR** – Breaking API changes
- **MINOR** – New features (backward compatible)
- **PATCH** – Bug fixes and internal improvements

Example:
- `1.0.0` → Initial stable release
- `1.1.0` → New features
- `1.0.1` → Bug fixes

## 🤝 Contributing
Contributions are welcome!
#### How to contribute
- Fork the repository
- Create a feature branch
- Make your changes
- Open a Pull Request
#### Please ensure:
- Code is clean and documented
- No secrets or credentials are added
- Native changes are ABI-safe

## 💬 Community & Support
Join our Discord to:
- Ask questions
- Share feedback
- Report issues
- Discuss features
#### 👉 Discord: https://discord.gg/N5KQseReFg

## 📄 License
This project is open source.
See the `LICENSE` file for details.

## ⭐ Acknowledgements
Built with a focus on:
- Stability
- Performance
- Android future compatibility

If you find this SDK useful, consider giving the repo a ⭐.


## 🔒 Security Notes

- Vyom Tunnel does not collect personal user data by default
- All VPN traffic handling happens locally on-device
- Host applications are responsible for server-side security and policies




