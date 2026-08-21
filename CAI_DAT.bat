@echo off
setlocal
chcp 65001 >nul
cd /d "%~dp0"

title FocusLock V5 - Cai dat 1 click

:: Tu yeu cau quyen Administrator neu chua co.
net session >nul 2>&1
if %errorlevel% neq 0 (
    echo Dang yeu cau quyen Administrator...
    powershell.exe -NoProfile -ExecutionPolicy Bypass -Command "Start-Process cmd.exe -ArgumentList '/c', '\"%~f0\"' -Verb RunAs"
    exit /b
)

echo.
echo ============================================================
echo   FOCUSLOCK V5 - CAI DAT 1 CLICK
echo ============================================================
echo.
echo Khong can cai .NET SDK vao o C.
echo SDK, NuGet cache, runtime va du lieu se nam trong thu muc code nay.
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0setup-oneclick.ps1"
set EXITCODE=%errorlevel%

echo.
if not "%EXITCODE%"=="0" (
    echo [LOI] Cai dat khong thanh cong. Xem dong bao loi phia tren.
    echo.
    pause
    exit /b %EXITCODE%
)

echo [OK] FocusLock da duoc cai dat.
echo Co the dong cua so nay.
echo.
pause
exit /b 0
