# Changelog
## [1.0.1] - 2026-02-18
#### Added
- Dynamic Branding: New APIs (setAppName, setAppIcon) to allow developers to customize the VPN notification identity.
- Automatic Fallback: Intelligent detection of host app name and icon if no custom branding is provided.
- Seamless Permission Chaining: The connectWithPermission API now automatically handles both Android 13+ POST_NOTIFICATIONS and VpnService permission requests in a single flow.
- Extended IP Analytics: Expanded VyomIpInfo model to include ISP, Organization, Region, City, and Flag URLs.
#### Fixed
- WebView Multi-Process Crash: Implemented setDataDirectorySuffix to prevent crashes when the host app and the VPN process initialize WebViews simultaneously (crucial for apps using Google Ads).
- Android 14/15 Compatibility: Standardized internal Broadcast Receivers with RECEIVER_EXPORTED flags for strict inter-process communication.

## [1.0.0] - 2026-02-08
- Initial stable release.
- Native Xray-based VPN core integration.
- Split tunneling (App Exclusion) support.
- System-level Kill Switch implementation.
- Auto-reconnect on network change and Boot persistence.
- 16 KB page size aligned native binaries for high-performance and Android 15 support.
