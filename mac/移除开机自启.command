#!/bin/bash
# Mac 端取消开机自启（对应 Windows 的 移除开机自启.bat）
cd "$(dirname "$0")"
./PineKVM-DisplaySwitcher-mac --remove
echo
read -r -p "Press Enter to close" _
