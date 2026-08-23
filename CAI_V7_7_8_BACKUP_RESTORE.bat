@echo off
setlocal
title FocusLock V7.7.8 - Backup Restore

net session >nul 2>&1
if errorlevel 1 (
  echo [LOI] Mo CMD bang Run as administrator.
  pause
  exit /b 1
)

set "PKG=%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%PKG%CAI_V7_7_8_BACKUP_RESTORE.ps1"
set "RC=%ERRORLEVEL%"

echo.
if not "%RC%"=="0" (
  echo [LOI] V7.7.8 that bai. Source/runtime duoc rollback khi co the.
  echo Ma loi: %RC%
  pause
  exit /b %RC%
)

echo [OK] FocusLock V7.7.8 da cai dat.
pause
exit /b 0
