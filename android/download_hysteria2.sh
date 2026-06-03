#!/bin/bash
# Script to download Hysteria2 binary for Android arm64-v8a
# Run this script on a machine with internet access, then copy the file to the project

HYSTERIA_VERSION="v2.9.2"
DOWNLOAD_URL="https://github.com/apernet/hysteria/releases/download/app/${HYSTERIA_VERSION}/hysteria-android-arm64"
OUTPUT_FILE="libhysteria2.so"
TARGET_DIR="temp_vyom_tunnel/vyom-tun-sdk/src/main/jniLibs/arm64-v8a"

echo "Downloading Hysteria2 ${HYSTERIA_VERSION} for Android arm64..."
curl -L -o "${OUTPUT_FILE}" "${DOWNLOAD_URL}"

if [ $? -eq 0 ]; then
    echo "Downloaded: $(ls -la ${OUTPUT_FILE})"
    
    if [ -d "${TARGET_DIR}" ]; then
        cp "${OUTPUT_FILE}" "${TARGET_DIR}/${OUTPUT_FILE}"
        echo "Copied to ${TARGET_DIR}/${OUTPUT_FILE}"
    else
        echo "Target directory not found. Please copy ${OUTPUT_FILE} to:"
        echo "  ${TARGET_DIR}/"
    fi
else
    echo "Download failed!"
    exit 1
fi
