# Project-specific R8 rules. Kept intentionally minimal: dependencies ship
# their own consumer rules and the release build verifies this file exists.

# ML Kit (play-services-code-scanner) discovers its component registrars from
# manifest <meta-data> and instantiates them reflectively. R8 full mode (AGP 9
# default) strips their no-arg constructors, so registration silently fails and
# GmsBarcodeScanning.getClient() crashes the app on first composition with an
# NPE (Object.getClass() on a null SharedPrefManager). Fixed in v1.4.24.
-keep class * implements com.google.firebase.components.ComponentRegistrar { public <init>(); }
-keep class com.google.mlkit.**.*Registrar { public <init>(); }
