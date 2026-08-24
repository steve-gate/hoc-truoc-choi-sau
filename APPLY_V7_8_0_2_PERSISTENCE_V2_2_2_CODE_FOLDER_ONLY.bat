@echo off
setlocal
cd /d "%~dp0"
net session >nul 2>&1
if not "%errorlevel%"=="0" (
  echo [ERROR] Run this BAT as Administrator.
  pause
  exit /b 1
)
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0APPLY_V7_8_0_2_PERSISTENCE_V2_2_2_CODE_FOLDER_ONLY.ps1"
set "RC=%errorlevel%"
echo.
if not "%RC%"=="0" echo [ERROR] Persistence V2.2.2 failed with code %RC%.
if "%RC%"=="0" echo [OK] Persistence V2.2.2 finished.
pause
exit /b %RC%
