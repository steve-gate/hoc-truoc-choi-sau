@echo off
setlocal
cd /d "%~dp0"
net session >nul 2>&1
if errorlevel 1 (
  echo Requesting Administrator rights...
  powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process -FilePath '%~f0' -Verb RunAs"
  exit /b
)
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0FIX_FINAL_FIX3_UI_BOOTSTRAP.ps1"
set ERR=%ERRORLEVEL%
echo.
if not "%ERR%"=="0" echo [ERROR] UI/bootstrap repair failed with code %ERR%.
pause
exit /b %ERR%
