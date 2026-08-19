#!/bin/bash
# Mac 端后台启动（对应 Windows 的 启动.bat；会自动先停旧实例再启动新实例）
cd "$(dirname "$0")"
nohup ./PineKVM-DisplaySwitcher-mac >/dev/null 2>&1 &
sleep 1
PID=$(pgrep -x PineKVM-DisplaySwitcher-mac | head -1)
if [ -n "$PID" ]; then
  echo "PineKVM-DisplaySwitcher-mac started (PID $PID). Log: $(pwd)/PineKVM-DisplaySwitcher.log"
else
  echo "Startup failed. Check log: $(pwd)/PineKVM-DisplaySwitcher.log"
fi
echo
read -r -p "Press Enter to close" _
