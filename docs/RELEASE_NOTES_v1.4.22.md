# ANARISE VPN v1.4.22 (draft)

## Fixes

- Prevent macOS startup from waiting synchronously on Keychain access. The encryption key is read from the existing user-only local keystore; if none exists, a new key is created with owner-only permissions.

## Build status

- Windows x64: pending local .NET 8 publish.
- macOS Apple Silicon (ARM64): build on the repository's macOS GitHub Actions runner.
