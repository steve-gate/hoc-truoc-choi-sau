@echo off
setlocal
cd /d "%~dp0"
title FocusLock V7 - Side by Side NativeHost

net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo [LOI] Mo CMD bang Run as administrator, sau do chay:
    echo.
    echo cd /d "%~dp0"
    echo HOAN_TAT_V7_KHONG_BUILD.bat
    echo.
    pause
    exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0hoan-tat-v7-khong-build.ps1"
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" (
    echo [LOI] Hoan tat V7 that bai. Ma loi: %RC%
    pause
    exit /b %RC%
)
echo [OK] V7 da hoan tat.
pause
exit /b 0
