cmake_minimum_required(VERSION 3.10.2)
project("vyom-v2ray")
set(CMAKE_SHARED_LINKER_FLAGS "${CMAKE_SHARED_LINKER_FLAGS} -Wl,-z,max-page-size=16384")

# 1. Tell the linker WHERE to look for the .so files (the search path)
# We use the short names: xray and hev-socks5-tunnel
link_directories(${CMAKE_SOURCE_DIR}/../jniLibs/${ANDROID_ABI})

# 2. Create your bridge library
add_library(vyom-v2ray SHARED vyom-bridge.cpp)

# 3. Find the Android logging library
find_library(log-lib log)

# 4. LINK BY SHORT NAME
# This ensures it looks for "libxray.so" on the phone, not a Windows path
target_link_libraries(
        vyom-v2ray
        xray
        hev-socks5-tunnel
        ${log-lib}
)