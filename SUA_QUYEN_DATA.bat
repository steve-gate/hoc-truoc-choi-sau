@echo off
setlocal
cd /d "%~dp0"
title FocusLock - Sua quyen Data

net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo [LOI] Mo CMD bang Run as administrator, sau do chay:
    echo.
    echo cd /d "%~dp0"
    echo SUA_QUYEN_DATA.bat
    echo.
    pause
    exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0sua-quyen-data.ps1"
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" (
    echo [LOI] Sua quyen Data that bai. Ma loi: %RC%
    pause
    exit /b %RC%
)
echo [OK] FocusLock Guard da hoat dong.
pause
exit /b 0
