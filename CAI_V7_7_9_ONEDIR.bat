@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
title FocusLock V7.7.9 - OneDir
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0CAI_V7_7_9_ONEDIR.ps1"
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" (
  echo [LOI] V7.7.9 OneDir build that bai. Runtime cu khong bi thay doi.
) else (
  echo [OK] Mo FocusLock-OneDir\FocusLock.exe de chay.
)
pause
exit /b %RC%
