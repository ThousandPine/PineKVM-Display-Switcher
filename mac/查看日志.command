#!/bin/bash
# Mac 端查看日志（对应 Windows 的 查看日志.bat；用文本编辑打开）
cd "$(dirname "$0")"
if [ -f PineKVM-DisplaySwitcher.log ]; then
  open -e PineKVM-DisplaySwitcher.log
else
  echo "Log not found yet: $(pwd)/PineKVM-DisplaySwitcher.log"
  echo
  read -r -p "Press Enter to close" _
fi
