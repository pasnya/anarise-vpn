# PENDING RELEASE: Automatic Version Update Notification

## Status: IMPLEMENTED LOCALLY (NOT YET DEPLOYED TO GITHUB)

An automatic version check and update alert notification has been implemented in the codebase but has **not** been pushed to GitHub or built as a release APK yet. This was done to prevent premature notifications to users since the current live release on GitHub is still `v1.0.1`.

---

## What was Implemented

1. **Update Checker in ViewModel**:
   - File: [MainViewModel.kt](file:///Users/fokl/Documents/anty/vless2/app/src/main/java/com/example/vlessvpn/MainViewModel.kt)
   - Added: `checkForUpdates(currentVersion: String, onNewVersionAvailable: (String, String) -> Unit)`
   - Details: Queries `https://api.github.com/repos/pasnya/anarise-vpn/releases/latest` inside the `IO` thread coroutine, parses the latest release tag name, compares it with the running client version, and resolves the direct APK asset URL or fallback release page URL on a match.

2. **Dashboard UI alert**:
   - File: [DashboardScreen.kt](file:///Users/fokl/Documents/anty/vless2/app/src/main/java/com/example/vlessvpn/ui/screens/DashboardScreen.kt)
   - Added: `LaunchedEffect` trigger on start which queries `checkForUpdates` with the current running version (hardcoded as `currentVersion = "1.0.1"`).
   - Added: A premium `AlertDialog` which pops up if a newer version tag is found on GitHub, providing:
     - **"Скачать" button**: Opens the direct download URL in the device's browser to fetch the new APK.
     - **"Позже" button**: Closes the dialog.

---

## Instructions for the Next Developer / AI Agent

When you compile and deploy the next release (e.g. `v1.0.2` or higher), perform the following steps to activate the update checker for all `v1.0.1` users:

1. **Increment App Version in Gradle**:
   - Open [app/build.gradle.kts](file:///Users/fokl/Documents/anty/vless2/app/build.gradle.kts)
   - Update `versionCode` (e.g. to `3`) and `versionName` (e.g. to `"1.0.2"`).

2. **Increment hardcoded version in UI**:
   - Open [DashboardScreen.kt](file:///Users/fokl/Documents/anty/vless2/app/src/main/java/com/example/vlessvpn/ui/screens/DashboardScreen.kt)
   - Update `currentVersion` (e.g. to `"1.0.2"`).

3. **Compile the signed release APK**:
   - Run: `./gradlew assembleRelease`

4. **Commit and push code**:
   - Stage and commit the local changes.
   - Push to main branch.

5. **Deploy the Release on GitHub**:
   - Create a new tag `v1.0.2` on GitHub.
   - Upload the signed `app-release.apk` as a release asset (name it `anarise-vpn-v1.0.2.apk`).
