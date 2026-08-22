@echo off
setlocal
cd /d "%~dp0"
title FocusLock - Cai dat

net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo [LOI] Hay CHUOT PHAI file nay va chon:
    echo       Run as administrator
    echo.
    pause
    exit /b 1
)

echo.
echo ==========================================
echo FOCUSLOCK - CAI DAT
echo ==========================================
echo Administrator: OK
echo.

if not exist "%~dp0setup-oneclick.ps1" (
    echo [LOI] Khong tim thay setup-oneclick.ps1
    echo.
    pause
    exit /b 2
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-oneclick.ps1"
set "RC=%ERRORLEVEL%"

echo.
if not "%RC%"=="0" (
    echo [LOI] Cai dat that bai. Ma loi: %RC%
    if exist "%~dp0install.log" echo Log: %~dp0install.log
    echo.
    pause
    exit /b %RC%
)

echo [OK] Cai dat hoan tat.
echo.
pause
exit /b 0
