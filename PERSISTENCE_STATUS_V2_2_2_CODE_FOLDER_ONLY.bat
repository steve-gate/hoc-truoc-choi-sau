@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0PERSISTENCE_STATUS_V2_2_2_CODE_FOLDER_ONLY.ps1"
pause
