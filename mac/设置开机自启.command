#!/bin/bash
# Mac 端开机自启（对应 Windows 的 设置开机自启.bat；先移除旧注册再重新注册，可重复点击）
cd "$(dirname "$0")"
./PineKVM-DisplaySwitcher-mac --remove >/dev/null 2>&1
./PineKVM-DisplaySwitcher-mac --install
echo
read -r -p "Press Enter to close" _
