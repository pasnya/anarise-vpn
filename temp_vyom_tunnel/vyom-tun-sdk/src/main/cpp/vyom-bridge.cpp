#include <jni.h>
#include <android/log.h>
#include <unistd.h>
#include <stdio.h>

static JavaVM *g_jvm = nullptr;
static jobject g_vpnService = nullptr;

// These are functions from your Go and Tunnel libraries
extern "C" {
int XrayMain(const char *configJson, const char *assetPath);
void XrayStop();
char *XrayTestConfig(const char *configJson, const char *assetPath);
void XrayFreeString(char *str);
void hev_socks5_tunnel_main(const char *config_path, int fd);
void hev_socks5_tunnel_quit(); // Corrected function name based on hev-tun standard
}

// All JNI functions MUST be inside this block in a .cpp file
extern "C" {

JNIEXPORT jint JNICALL JNI_OnLoad(JavaVM *vm, void *reserved) {
    g_jvm = vm;
    return JNI_VERSION_1_6;
}

// Function called by Xray (Go) to protect its sockets
__attribute__((visibility("default"))) bool go_protect_socket(int fd) {
    if (g_jvm == nullptr || g_vpnService == nullptr) return false;

    JNIEnv *env;
    jint res = g_jvm->GetEnv((void**)&env, JNI_VERSION_1_6);
    bool shouldDetach = false;
    if (res == JNI_EDETACHED) {
        if (g_jvm->AttachCurrentThread(&env, nullptr) != JNI_OK) return false;
        shouldDetach = true;
    }

    jclass serviceClass = env->GetObjectClass(g_vpnService);
    // Find the protect method in the base VpnService class
    jmethodID protectMethod = env->GetMethodID(serviceClass, "protect", "(I)Z");
    jboolean success = env->CallBooleanMethod(g_vpnService, protectMethod, fd);

    if (shouldDetach) g_jvm->DetachCurrentThread();
    return success;
}

JNIEXPORT void JNICALL
Java_io_github_vyomtunnel_core_NativeEngine_initNative(JNIEnv *env, jobject thiz,
                                                       jobject vpnService) {
    if (g_vpnService != nullptr) env->DeleteGlobalRef(g_vpnService);
    g_vpnService = env->NewGlobalRef(vpnService);
}

JNIEXPORT jint JNICALL
Java_io_github_vyomtunnel_core_NativeEngine_startXray(JNIEnv *env, jobject thiz, jstring config,
                                                      jstring asset_path) {
    const char *nativeConfig = env->GetStringUTFChars(config, 0);
    const char *nativePath = env->GetStringUTFChars(asset_path, 0);
    int result = XrayMain(nativeConfig, nativePath);
    env->ReleaseStringUTFChars(config, nativeConfig);
    env->ReleaseStringUTFChars(asset_path, nativePath);
    return result;
}

JNIEXPORT void JNICALL
Java_io_github_vyomtunnel_core_NativeEngine_stopXray(JNIEnv *env, jobject thiz) {
    XrayStop();
}

JNIEXPORT jstring JNICALL
Java_io_github_vyomtunnel_core_NativeEngine_validateConfig(JNIEnv *env, jobject thiz,
                                                           jstring config, jstring asset_path) {
    const char *nativeConfig = env->GetStringUTFChars(config, 0);
    const char *nativePath = env->GetStringUTFChars(asset_path, 0);

    char *error = XrayTestConfig(nativeConfig, nativePath);

    env->ReleaseStringUTFChars(config, nativeConfig);
    env->ReleaseStringUTFChars(asset_path, nativePath);

    if (error == nullptr) return nullptr;
    jstring result = env->NewStringUTF(error);
    XrayFreeString(error);
    return result;
}

// Keep this for backward compatibility if any old code calls it
bool protect_socket(JNIEnv *env, int fd) {
    return go_protect_socket(fd);
}

}
