#!/bin/bash
# Mac 端查看状态（对应 Windows 的 查看状态.bat）
cd "$(dirname "$0")"
echo "== running processes =="
pgrep -lf PineKVM-DisplaySwitcher-mac || echo "(not running)"
echo "== LaunchAgent =="
launchctl list | grep com.pinekvm || echo "(LaunchAgent not loaded)"
echo
read -r -p "Press Enter to close" _
