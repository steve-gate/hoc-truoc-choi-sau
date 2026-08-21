@echo off
cd /d "%~dp0"
if exist "publish\App\FocusLock.exe" (
  start "" "publish\App\FocusLock.exe"
) else (
  echo FocusLock chua duoc build/cai. Hay chay CAI_DAT.bat truoc.
  pause
)
