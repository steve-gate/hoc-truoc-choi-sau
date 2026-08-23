@echo off
setlocal
cd /d "%~dp0"
title FocusLock V7.2 - Website + Weekly Calendar + Real Profiles

net session >nul 2>&1
if errorlevel 1 (
  echo.
  echo [LOI] Mo CMD bang Run as administrator, sau do chay:
  echo.
  echo cd /d "%~dp0"
  echo NANG_CAP_V7_2.bat
  echo.
  pause
  exit /b 1
)

if not exist "%~dp0setup-oneclick.ps1" (
  echo [LOI] Khong tim thay setup-oneclick.ps1
  pause
  exit /b 2
)

echo.
echo ==========================================
echo FOCUSLOCK V7.2
echo WEBSITE + WEEKLY CALENDAR + REAL PROFILES
echo ==========================================
echo.

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-oneclick.ps1"
set "RC=%ERRORLEVEL%"

echo.
if not "%RC%"=="0" (
  echo [LOI] Nang cap V7.2 that bai. Ma loi: %RC%
  if exist "%~dp0install.log" echo Log: %~dp0install.log
  pause
  exit /b %RC%
)

echo [OK] FocusLock V7.2 da build + cai dat xong.
echo [NHAC] Vao chrome://extensions hoac edge://extensions va Reload FocusLock Browser Bridge mot lan.
echo.
pause
exit /b 0
