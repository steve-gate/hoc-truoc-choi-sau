@echo off
setlocal
cd /d "%~dp0"
title FocusLock Guard Diagnostic

net session >nul 2>&1
if errorlevel 1 (
  echo [LOI] Mo CMD bang Run as administrator, sau do chay:
  echo.
  echo cd /d "%~dp0"
  echo CHUAN_DOAN_GUARD.bat
  echo.
  pause
  exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0CHUAN_DOAN_GUARD.ps1"
echo.
pause
