@echo off
(
echo Set UAC = CreateObject^("Shell.Application"^)
echo UAC.ShellExecute "cmd.exe", "/c """"%~f0""""", "", "runas", 1
) > "%temp%\getadmin.vbs"
type "%temp%\getadmin.vbs"
