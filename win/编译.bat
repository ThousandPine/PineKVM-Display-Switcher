@echo off
echo Stopping running PineKVM-DisplaySwitcher if any...
powershell.exe -NoProfile -Command "Stop-Process -Name PineKVM-DisplaySwitcher -Force -ErrorAction SilentlyContinue"
"C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /target:winexe /optimize+ /out:"%~dp0PineKVM-DisplaySwitcher.exe" "%~dp0PineKVM-DisplaySwitcher.cs"
echo Build done: %~dp0PineKVM-DisplaySwitcher.exe
pause
