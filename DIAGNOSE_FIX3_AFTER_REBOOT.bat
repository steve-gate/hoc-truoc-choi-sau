@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0DIAGNOSE_FIX3_AFTER_REBOOT.ps1"
set EC=%ERRORLEVEL%
echo.
if not "%EC%"=="0" echo [ERROR] Diagnostic returned code %EC%.
pause
exit /b %EC%
