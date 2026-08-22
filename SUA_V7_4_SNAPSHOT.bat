@echo off
setlocal
cd /d "%~dp0"
title FocusLock V7.4.1 - ServiceSnapshot Fix

echo.
echo ==========================================
echo FOCUSLOCK V7.4.1 - SNAPSHOT FIX
echo ==========================================
echo.

if not exist "%~dp0FocusLock.Shared\Protocol\ServiceSnapshot.cs" (
    echo [LOI] Khong tim thay ServiceSnapshot.cs
    pause
    exit /b 2
)

findstr /C:"EntertainmentSessionActive" "%~dp0FocusLock.Shared\Protocol\ServiceSnapshot.cs" >nul
if errorlevel 1 (
    echo [LOI] ServiceSnapshot.cs van la ban cu.
    pause
    exit /b 3
)

echo [OK] ServiceSnapshot V7.4 da dung.
echo.
echo Dang xoa cache build cu...

for %%D in (
  "FocusLock.Shared\bin"
  "FocusLock.Shared\obj"
  "FocusLock.App\bin"
  "FocusLock.App\obj"
) do (
  if exist "%~dp0%%~D" rmdir /s /q "%~dp0%%~D"
)

echo [OK] Da xoa bin/obj cua Shared + App.
echo.

if not exist "%~dp0NANG_CAP_V7_4.bat" (
    echo [LOI] Khong tim thay NANG_CAP_V7_4.bat
    pause
    exit /b 4
)

call "%~dp0NANG_CAP_V7_4.bat"
exit /b %ERRORLEVEL%
