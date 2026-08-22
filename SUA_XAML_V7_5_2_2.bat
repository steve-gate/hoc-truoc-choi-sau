@echo off
setlocal
cd /d "%~dp0"
title FocusLock V7.5.2.2 - XAML Resource Fix

net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo [LOI] Mo CMD bang Run as administrator, sau do chay:
    echo.
    echo cd /d "%~dp0"
    echo SUA_XAML_V7_5_2_2.bat
    echo.
    pause
    exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0sua-xaml-v7-5-2-2.ps1"
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" (
    echo [LOI] Sua XAML that bai. Ma loi: %RC%
    pause
    exit /b %RC%
)

echo [OK] FocusLock V7.5.2.2 da mo.
pause
exit /b 0
