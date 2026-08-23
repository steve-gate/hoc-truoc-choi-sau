@echo off
setlocal
cd /d "%~dp0"
title FocusLock V7.6.2 - Rollback Web Core to V7.3

net session >nul 2>&1
if errorlevel 1 (
  echo [LOI] Mo CMD bang Run as administrator.
  pause
  exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0nang-cap-v7-6-2.ps1"
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" (
  echo [LOI] Nang cap V7.6.2 that bai. Ma loi: %RC%
  pause
  exit /b %RC%
)
echo [OK] V7.6.2 - Web Core da rollback ve co che V7.3.
pause
exit /b 0
