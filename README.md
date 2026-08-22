# FocusLock V7.4 — Profile-First + Entertainment Bubble + 24h Keys

V7.4 sửa ba điểm cốt lõi của V7.3:

1. Bubble giải trí dùng trạng thái session thật từ Guard, áp dụng cho cả app và website; hiển thị thời gian còn dùng được, nguồn allowance/ví và profile hiện tại.
2. Key có hạn tối thiểu 24 giờ ở cả UI, model, Guard, factory và migration. Key cũ chưa dùng có hạn ngắn hơn sẽ được nâng lên tối thiểu 24 giờ và ký lại HMAC.
3. Quản lý giải trí theo Profile trước: Profile Center là nơi chính để quản lý App + Website, policy ngoài lịch/trong lịch, allowance, lịch tuần và cách khóa app mặc định. Thiết lập riêng từng app chỉ còn là override nâng cao.

## Nâng cấp

Giải nén gói UPDATE ghi đè vào thư mục source hiện tại, không xóa `publish\Data`, sau đó mở CMD Administrator và chạy:

```bat
NANG_CAP_V7_4.bat
```

Sau nâng cấp, Reload FocusLock Browser Bridge trong `chrome://extensions` hoặc `edge://extensions` một lần.


## V7.5
- Bubble giải trí website dùng foreground browser được Guard xác minh.
- Bảo vệ cấu hình bằng đoạn văn hoặc khung thời gian.
- Minimize/Close to system tray.
