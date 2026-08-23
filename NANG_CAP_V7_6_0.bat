@echo off
setlocal
cd /d "%~dp0"
title FocusLock V7.6.0 - Browser Core Fix

net session >nul 2>&1
if errorlevel 1 (
  echo [LOI] Mo CMD bang Run as administrator.
  pause
  exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0nang-cap-v7-6-0.ps1"
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" (
  echo [LOI] Nang cap V7.6.0 that bai. Ma loi: %RC%
  pause
  exit /b %RC%
)
echo [OK] FocusLock V7.6.0 da cai dat.
pause
exit /b 0
