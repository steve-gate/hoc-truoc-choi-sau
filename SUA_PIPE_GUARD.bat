@echo off
setlocal
cd /d "%~dp0"
title FocusLock - Sua Guard Pipe

net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo [LOI] Can quyen Administrator.
    echo Mo CMD bang "Run as administrator", sau do chay:
    echo.
    echo cd /d "%~dp0"
    echo SUA_PIPE_GUARD.bat
    echo.
    pause
    exit /b 1
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0sua-pipe-guard.ps1"
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" (
    echo [LOI] Guard Pipe fix that bai. Ma loi: %RC%
    echo Neu co file publish\Logs\service-pipe.log, gui file do de kiem tra.
    pause
    exit /b %RC%
)
echo [OK] Guard va Named Pipe da hoat dong.
pause
exit /b 0
