@echo off
powershell.exe -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='PineKVM-DisplaySwitcher.exe'\" | Select-Object ProcessId, Name, CreationDate | Format-List"
echo (If nothing is listed above, the watcher is not running.)
pause
