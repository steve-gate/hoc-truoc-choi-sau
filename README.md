# FocusLock V5 — One-Click / Code-Folder Edition

Bản này được làm lại để cài đặt nhanh và không yêu cầu bạn tự cài .NET SDK vào Windows.

## Cài đặt — chỉ 1 file

1. Giải nén FocusLock vào ổ còn nhiều dung lượng, ví dụ `D:\Code\FocusLock`.
2. Chuột phải **`CAI_DAT.bat`** → **Run as administrator**.
3. Chờ script hoàn tất.

`CAI_DAT.bat` tự làm toàn bộ phần còn lại:

- Nếu máy chưa có .NET SDK, tự tải **.NET SDK 10 x64** vào `.tools\dotnet` ngay trong thư mục code.
- Đưa NuGet cache, .NET CLI home và build temp vào `.tools` trong thư mục code để giảm ghi dữ liệu lên ổ C.
- Restore + build App, Windows Service và Native Host.
- Kiểm tra thật sự các file `.exe` đã được tạo; build lỗi thì dừng ngay, không báo hoàn tất giả.
- Đăng ký Windows Service, startup và Native Messaging Host.
- Khởi động FocusLock.
- Mở thư mục Browser Extension và trang Extensions để bạn load extension nếu cần V5 trình duyệt.

## Thư mục sau khi cài

```text
<THƯ MỤC CODE>\
├─ CAI_DAT.bat
├─ setup-oneclick.ps1
├─ .tools\                 # SDK + NuGet/cache/build temp, nằm cùng ổ code
└─ publish\
   ├─ App\
   ├─ Service\
   ├─ NativeHost\
   ├─ BrowserExtension\
   └─ Data\                # key, balance, settings, statistics, HMAC state
```

Không copy FocusLock vào `C:\Program Files\FocusLock` và không dùng `C:\ProgramData\FocusLock` cho dữ liệu chính.

Windows vẫn phải lưu vài registry/service entries rất nhỏ. Windows và các chương trình khác vẫn có thể tự dùng một lượng nhỏ TEMP/cache hệ thống; FocusLock đã chuyển phần build/cache lớn của chính nó sang `.tools`.

## Browser Extension

Core FocusLock cài tự động. Browser Extension của Chrome/Edge ở chế độ phát triển vẫn cần load một lần:

- Chrome: `chrome://extensions`
- Edge: `edge://extensions`
- Bật **Developer mode**
- Chọn **Load unpacked**
- Chọn `<THƯ MỤC CODE>\publish\BrowserExtension`

Installer sẽ tự mở thư mục này để thao tác nhanh hơn.

## Chạy lại / cập nhật

Chỉ cần chạy lại **`CAI_DAT.bat`**. Dữ liệu trong `publish\Data` được giữ nguyên.

## Gỡ đăng ký khỏi Windows

Mở PowerShell Administrator trong `publish` và chạy `uninstall-v5.ps1`. Script không xóa `Data`.

## Lưu ý

Sau khi cài, không di chuyển/đổi tên cả thư mục FocusLock vì Windows Service và Native Messaging lưu đường dẫn tuyệt đối. Muốn chuyển ổ: uninstall trước, di chuyển folder, rồi chạy lại `CAI_DAT.bat`.
