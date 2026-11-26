# WinPilot — Trung tâm Tự động hóa Windows

Ứng dụng desktop tập trung cho Windows giúp bạn:
1. **Khởi động cùng Windows** (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` với cờ `--startup`), chạy ẩn dưới khay hệ thống (System Tray).
2. **Tự động mở các ứng dụng bạn thiết lập** (`.exe`, `.lnk`, `.bat`, thư mục, hoặc URL) hỗ trợ kéo-thả, chọn từ Start Menu, độ trễ từng app, chạy quyền Admin, và bỏ qua nếu đang chạy sẵn.
3. **Gửi thông báo Telegram song song khi bật máy** (tự động chờ & thử lại khi Wi-Fi/mạng mới kết nối lúc khởi động).
4. **Gửi thông báo Telegram trước khi máy tắt / khởi động lại / đăng xuất** thông qua cơ chế Win32 `WM_QUERYENDSESSION` + `WM_ENDSESSION` + `ShutdownBlockReasonCreate` với độ ưu tiên tắt máy cao (`SetProcessShutdownParameters(0x3FF, 0)`).

## Chạy ứng dụng

- **File chạy trực tiếp (Release):** `dist\WinPilot.exe`
- **Build & Test từ mã nguồn:**
  ```powershell
  dotnet build WinPilot.sln
  dotnet test WinPilot.sln
  ```

## Hướng dẫn cấu hình nhanh

1. Mở `WinPilot.exe`.
2. Vào tab **Ứng dụng khởi động** để thêm các phần mềm cần tự mở (kéo thả `.exe`/`.lnk` vào hoặc bấm **+ Chọn từ Start Menu**).
3. Vào tab **Telegram & Thông báo**:
   - Dán **Bot Token** từ `@BotFather` và bấm **Kiểm tra Bot**. Token được mã hóa bảo mật theo tài khoản Windows (DPAPI) khi lưu vào `%AppData%\WinPilot\config.json`.
   - Nhập **Chat ID** (hỗ trợ nhiều ID cách nhau bởi dấu phẩy) hoặc nhắn `/start` cho Bot rồi bấm **Tìm Chat ID tự động**.
   - Bấm **Test gửi ngay** và **Giả lập tắt máy (Test)** để kiểm tra tin nhắn.
4. Vào tab **Tổng quan** hoặc **Cài đặt hệ thống**, bật **Tự động khởi động WinPilot cùng Windows**.
