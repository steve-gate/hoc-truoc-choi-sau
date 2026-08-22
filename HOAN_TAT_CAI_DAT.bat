@echo off
setlocal
cd /d "%~dp0"
title FocusLock - Hoan tat cai dat

net session >nul 2>&1
if errorlevel 1 (
    echo.
    echo [LOI] Cua so nay chua co quyen Administrator.
    echo Mo CMD bang Run as administrator, sau do chay:
    echo "%~f0"
    echo.
    pause
    exit /b 1
)

echo.
echo ==========================================
echo FOCUSLOCK - HOAN TAT CAI DAT (KHONG BUILD LAI)
echo ==========================================
echo.

if not exist "%~dp0publish\install-v5.ps1" (
    echo [LOI] Khong tim thay publish\install-v5.ps1
    echo Hay giai nen hotfix de dung cau truc thu muc.
    pause
    exit /b 2
)

powershell.exe -NoLogo -NoProfile -ExecutionPolicy Bypass -File "%~dp0publish\install-v5.ps1"
set "RC=%ERRORLEVEL%"

echo.
if not "%RC%"=="0" (
    echo [LOI] Hoan tat cai dat that bai. Ma loi: %RC%
    pause
    exit /b %RC%
)

echo [OK] FocusLock da duoc cai xong.
echo.
pause
exit /b 0
