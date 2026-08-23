@echo off
setlocal
cd /d "%~dp0"
title FocusLock V7.7.0 - Profile First UX

net session >nul 2>&1
if errorlevel 1 (
  echo [LOI] Mo CMD bang Run as administrator.
  pause
  exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0CAI_V7_7_0_PROFILE_FIRST.ps1"
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" (
  echo [LOI] Cai V7.7.0 that bai. Ma loi: %RC%
  pause
  exit /b %RC%
)
echo [OK] FocusLock V7.7.0 Profile-first da cai dat.
pause
exit /b 0
