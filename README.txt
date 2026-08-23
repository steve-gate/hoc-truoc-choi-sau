FOCUSLOCK V7.7.9 - ONEDIR WIN-X64
=================================

MUC TIEU
- Tao mot thu muc FocusLock-OneDir doc lap.
- File chay chinh nam ngay root: FocusLock.exe.
- App/Service/NativeHost deu self-contained .NET 10 x64.
- Khong can BAT/PowerShell de MO phan mem sau khi da build.

CAU TRUC DA BUILD
FocusLock-OneDir\
  FocusLock.exe
  Service\FocusLock.Service.exe
  NativeHost\FocusLock.NativeHost.exe
  BrowserExtension\
  Data\
  Logs\
  Install-OneDir.ps1
  cac DLL/runtime cua App

CACH TAO
1. Giai nen ZIP nay TRUC TIEP vao thu muc source FocusLock dang co FocusLock.sln.
2. Chay CAI_V7_7_9_ONEDIR.bat.
3. Script build sang FocusLock-OneDir, khong thay runtime publish cu.
4. Khi thanh cong, mo FocusLock-OneDir\FocusLock.exe.
5. Lan dau FocusLock.exe se xin Administrator 1 lan de dang ky Guard Service/Native Host.
6. Tu lan sau chi mo FocusLock.exe.

DU LIEU
- BUILD_ONEDIR se uu tien giu Data tu FocusLock-OneDir cu neu co.
- Neu chua co OneDir, no copy Data tu publish\Data hien tai sang OneDir.
- Backup/Restore V7.7.8 van duoc giu.

LUU Y
- Khong tach rieng FocusLock.exe khoi cac file/DLL trong OneDir.
- Co the di chuyen CA THU MUC OneDir sang o khac. Mo FocusLock.exe tai vi tri moi; lan dau tai vi tri moi app se xin Admin va cap nhat Service path.
- BrowserExtension dang load unpacked co the tiep tuc dung ban cu; de dong bo duong dan, co the Reload/Load unpacked tu FocusLock-OneDir\BrowserExtension.
