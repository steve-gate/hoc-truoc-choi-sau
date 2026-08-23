@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"
title FocusLock V7.7.9 - Build OneDir
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0BUILD_ONEDIR.ps1"
set "RC=%ERRORLEVEL%"
echo.
if not "%RC%"=="0" (
  echo [LOI] Tao OneDir that bai. Runtime FocusLock dang dung khong bi thay doi.
) else (
  echo [OK] Da tao FocusLock-OneDir\FocusLock.exe
)
pause
exit /b %RC%
