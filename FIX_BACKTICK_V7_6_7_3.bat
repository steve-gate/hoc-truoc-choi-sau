@echo off
setlocal
cd /d "%~dp0"
title FocusLock V7.6.7.3 - Backtick syntax hotfix

net session >nul 2>&1
if errorlevel 1 (
    echo [LOI] Mo CMD bang Run as administrator.
    pause
    exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0FIX_BACKTICK_V7_6_7_3.ps1"
set "RC=%ERRORLEVEL%"

echo.
if not "%RC%"=="0" (
    echo [LOI] Hotfix that bai. Ma loi: %RC%
    pause
    exit /b %RC%
)

echo [OK] FocusLock V7.6.7.3 da cai dat.
pause
exit /b 0
