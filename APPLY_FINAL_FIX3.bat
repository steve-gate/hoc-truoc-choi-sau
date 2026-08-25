@echo off
setlocal
cd /d "%~dp0"
net session >nul 2>&1
if not "%errorlevel%"=="0" (
  powershell.exe -NoProfile -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0APPLY_FINAL_FIX3.ps1"
set ERR=%ERRORLEVEL%
if not "%ERR%"=="0" echo [ERROR] FINAL FIX 3 failed with code %ERR%.
pause
exit /b %ERR%
