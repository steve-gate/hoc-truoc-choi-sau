@echo off
setlocal
cd /d "%~dp0"
title FocusLock - Chan doan Web 45 giay

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0CHUAN_DOAN_WEB.ps1"

echo.
pause
