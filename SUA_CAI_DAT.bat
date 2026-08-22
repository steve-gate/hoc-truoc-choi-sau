@echo off
setlocal EnableExtensions
cd /d "%~dp0"
call "%~dp0CAI_DAT.bat"
exit /b %ERRORLEVEL%
