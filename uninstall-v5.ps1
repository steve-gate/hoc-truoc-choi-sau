#Requires -RunAsAdministrator
$ErrorActionPreference = "SilentlyContinue"
$serviceName = "FocusLockGuard"
Stop-Service -Name $serviceName -Force
& sc.exe delete $serviceName | Out-Null
Get-Process -Name "FocusLock" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Get-Process -Name "FocusLock.NativeHost" -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "FocusLock" -ErrorAction SilentlyContinue
Remove-Item "HKLM:\Software\Google\Chrome\NativeMessagingHosts\com.focuslock.browserbridge" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "HKLM:\Software\Microsoft\Edge\NativeMessagingHosts\com.focuslock.browserbridge" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "HKLM:\Software\WOW6432Node\Google\Chrome\NativeMessagingHosts\com.focuslock.browserbridge" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item "HKLM:\Software\WOW6432Node\Microsoft\Edge\NativeMessagingHosts\com.focuslock.browserbridge" -Recurse -Force -ErrorAction SilentlyContinue
Write-Host "Đã gỡ đăng ký FocusLock V5 khỏi Windows." -ForegroundColor Yellow
Write-Host "Không xóa thư mục code/runtime và không xóa .\Data để bạn không mất cấu hình, key, balance và thống kê." -ForegroundColor Yellow
Write-Host "Nếu muốn xóa hoàn toàn, Remove extension trong Chrome/Edge rồi tự xóa thư mục FocusLock sau khi service đã được gỡ." -ForegroundColor DarkGray
