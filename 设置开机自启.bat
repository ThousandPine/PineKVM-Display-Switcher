@echo off
start "" /wait "%~dp0PineKVM-DisplaySwitcher.exe" --remove
start "" /wait "%~dp0PineKVM-DisplaySwitcher.exe" --install
echo Done. Startup shortcut refreshed (removed then re-registered).
pause
