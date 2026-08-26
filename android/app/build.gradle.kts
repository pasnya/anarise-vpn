plugins {
  alias(libs.plugins.android.application)
  alias(libs.plugins.compose.compiler)
}

val releaseStoreFile = providers.gradleProperty("ANARISE_STORE_FILE")
    .orElse(providers.environmentVariable("ANARISE_STORE_FILE"))
val releaseStorePassword = providers.gradleProperty("ANARISE_STORE_PASSWORD")
    .orElse(providers.environmentVariable("ANARISE_STORE_PASSWORD"))
val releaseKeyAlias = providers.gradleProperty("ANARISE_KEY_ALIAS")
    .orElse(providers.environmentVariable("ANARISE_KEY_ALIAS"))
val releaseKeyPassword = providers.gradleProperty("ANARISE_KEY_PASSWORD")
    .orElse(providers.environmentVariable("ANARISE_KEY_PASSWORD"))
val releaseSigningConfigured = listOf(
    releaseStoreFile.orNull,
    releaseStorePassword.orNull,
    releaseKeyAlias.orNull,
    releaseKeyPassword.orNull
).all { !it.isNullOrBlank() }

android {
    namespace = "com.example.vlessvpn"
    compileSdk = 36

    lint {
        checkReleaseBuilds = true
        abortOnError = true
    }
    defaultConfig {
        applicationId = "com.example.vlessvpn"
        minSdk = 24
        targetSdk = 36
        versionCode = 27
        versionName = "1.4.15"
        ndk {
            abiFilters.add("arm64-v8a")
        }
    }

    signingConfigs {
        create("release") {
            storeFile = file(releaseStoreFile.orNull ?: "missing-release-keystore")
            storePassword = releaseStorePassword.orNull ?: ""
            keyAlias = releaseKeyAlias.orNull ?: ""
            keyPassword = releaseKeyPassword.orNull ?: ""
        }
    }

    buildTypes {
        release {
            isMinifyEnabled = true
            signingConfig = signingConfigs.getByName("release")
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }
    compileOptions {
        sourceCompatibility = JavaVersion.VERSION_17
        targetCompatibility = JavaVersion.VERSION_17
    }
    buildFeatures {
      compose = true
      aidl = false
      buildConfig = false
      shaders = false
    }

    packaging {
      resources {
        excludes += "/META-INF/{AL2.0,LGPL2.1}"
      }
      jniLibs {
        useLegacyPackaging = true
      }
    }
}

tasks.matching { it.name == "validateSigningRelease" }.configureEach {
    doFirst {
        check(releaseSigningConfigured) {
            "Release signing requires ANARISE_STORE_FILE, ANARISE_STORE_PASSWORD, ANARISE_KEY_ALIAS, and ANARISE_KEY_PASSWORD."
        }
    }
}

kotlin {
    jvmToolchain(17)
}

dependencies {
  val composeBom = platform(libs.androidx.compose.bom)
  implementation(composeBom)
  androidTestImplementation(composeBom)

  // Core Android dependencies
  implementation(libs.androidx.core.ktx)
  implementation(libs.androidx.lifecycle.runtime.ktx)
  implementation(libs.androidx.activity.compose)

  // Arch Components
  implementation(libs.androidx.lifecycle.runtime.compose)
  implementation(libs.androidx.lifecycle.viewmodel.compose)

  // Compose
  implementation(libs.androidx.compose.ui)
  implementation(libs.androidx.compose.ui.tooling.preview)
  implementation(libs.androidx.compose.material3)
  // Tooling
  debugImplementation(libs.androidx.compose.ui.tooling)
  // Instrumented tests
  androidTestImplementation(libs.androidx.compose.ui.test.junit4)
  debugImplementation(libs.androidx.compose.ui.test.manifest)

  // Local tests: jUnit, coroutines, Android runner
  testImplementation(libs.junit)
  testImplementation(libs.kotlinx.coroutines.test)

  // Instrumented tests: jUnit rules and runners
  androidTestImplementation(libs.androidx.test.core)
  androidTestImplementation(libs.androidx.test.ext.junit)
  androidTestImplementation(libs.androidx.test.runner)
  androidTestImplementation(libs.androidx.test.espresso.core)

  // Vyom Tunnel SDK
  implementation(project(":vyom-tun-sdk"))

  // Google Play Services Code Scanner
  implementation(libs.play.services.code.scanner)
  implementation(libs.androidx.compose.material.icons.core)
}

