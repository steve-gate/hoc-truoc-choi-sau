@echo off
setlocal
cd /d "%~dp0"
title FocusLock - Sua Guard tu dong

net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo [LOI] Can quyen Administrator mot lan de sua Windows Service.
    echo Mo CMD bang Run as administrator, sau do chay:
    echo cd /d "%~dp0"
    echo SUA_GUARD_KHONG_BUILD.bat
    echo.
    pause
    exit /b 1
)

if not exist "%~dp0publish\Service\FocusLock.Service.exe" (
    echo [LOI] Khong tim thay publish\Service\FocusLock.Service.exe
    echo Hay chay CAI_DAT.bat truoc.
    pause
    exit /b 2
)

if not exist "%~dp0publish\install-v5.ps1" (
    echo [LOI] Khong tim thay publish\install-v5.ps1
    pause
    exit /b 3
)

echo Dang sua FocusLock Guard, khong build lai...
powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish\install-v5.ps1"
set "RC=%ERRORLEVEL%"

echo.
if not "%RC%"=="0" (
    echo [LOI] Sua Guard that bai. Ma loi: %RC%
    pause
    exit /b %RC%
)

echo [OK] Guard da duoc dat tu dong + recovery + pipe check.
echo Tu lan sau khong can vao services.msc nua.
pause
exit /b 0
