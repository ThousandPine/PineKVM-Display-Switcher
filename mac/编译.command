#!/bin/bash
# Mac 端一键编译（对应 Windows 的 win\编译.bat）
cd "$(dirname "$0")"
echo "Stopping running PineKVM-DisplaySwitcher-mac if any..."
pkill -x PineKVM-DisplaySwitcher-mac 2>/dev/null
echo "Compiling with swiftc..."
xcrun swiftc -O -swift-version 5 -o PineKVM-DisplaySwitcher-mac PineKVM-DisplaySwitcher-mac.swift
if [ $? -eq 0 ]; then
  echo "Build done: $(pwd)/PineKVM-DisplaySwitcher-mac"
else
  echo "Build FAILED (see errors above)."
fi
echo
read -r -p "Press Enter to close" _
