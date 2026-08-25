@echo off
cd /d "%~dp0"
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0VERIFY_FINAL_FIX2.ps1"
exit /b %ERRORLEVEL%
