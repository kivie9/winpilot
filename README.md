# WinPilot — Trung tâm Tự động hóa Windows

Ứng dụng desktop (WPF + WebView2) giúp tự động hóa quy trình khởi động ứng dụng và giám sát trạng thái nguồn / phiên làm việc trên Windows với thông báo qua Telegram & Webhook.

## Tính năng chính

1. **Khởi động cùng Windows** (`HKCU\Software\Microsoft\Windows\CurrentVersion\Run` với cờ `--startup`), chạy ẩn gọn nhẹ dưới khay hệ thống (System Tray).
2. **Tự động mở ứng dụng theo Profile**: hỗ trợ `.exe`, `.lnk`, `.bat`, thư mục hoặc URL; kéo-thả, quét từ Start Menu, thiết lập độ trễ từng app, chạy quyền Admin, bỏ qua nếu đang chạy sẵn, và điều kiện theo ngày trong tuần / Wi-Fi SSID.
3. **Thông báo Telegram & Webhook khi bật máy**: tự động chờ & gửi bù khi mạng/Wi-Fi kết nối chậm lúc khởi động.
4. **Giám sát nguồn & phiên Windows**:
   - **Tắt máy / Khởi động lại / Đăng xuất** (`Shutdown`): bắt tín hiệu Win32 `WM_QUERYENDSESSION` + `WM_ENDSESSION` + `ShutdownBlockReasonCreate` với độ ưu tiên cao (`SetProcessShutdownParameters(0x3FF, 0)`).
   - **Ngủ / Thức / Khóa / Mở khóa màn hình** (`Session`): theo dõi `Sleep`, `Wake`, `Lock`, `Unlock` và phân loại riêng dưới nhóm nhật ký `Session`.

## Tải về & Chạy ứng dụng

- **Tải bản dựng sẵn (khuyên dùng):** Vào mục **[Releases](https://github.com/kivie9/winpilot/releases/latest)** để tải `WinPilot.exe` (hoặc `WinPilot-win-x64.zip`).
  - Yêu cầu: Windows 10/11 (x64) và [.NET 8 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/8.0).
- **Build & Test từ mã nguồn:**
  ```powershell
  dotnet build WinPilot.sln
  dotnet test WinPilot.sln
  ```

## Hướng dẫn cấu hình nhanh

1. Mở `WinPilot.exe`.
2. Vào tab **Ứng dụng khởi động** để thêm phần mềm cần tự mở (kéo thả `.exe`/`.lnk` vào hoặc chọn từ Start Menu).
3. Vào tab **Thông báo & Kênh**:
   - Dán **Bot Token** từ `@BotFather` và bấm **Kiểm tra Bot**. Token được mã hóa bằng Windows DPAPI trước khi lưu tại `%AppData%\WinPilot\config.json`.
   - Nhập **Chat ID** (hỗ trợ nhiều ID cách nhau bởi dấu phẩy) hoặc nhắn `/start` cho Bot rồi bấm **Tìm Chat ID tự động**.
   - Bấm **Test gửi ngay** / **Giả lập tắt máy (Test)** để kiểm tra tin nhắn.
4. Vào tab **Tổng quan** hoặc **Cài đặt**, bật **Tự động khởi động cùng Windows**.

## Phát hành phiên bản mới (GitHub Actions)

Workflow tại `.github/workflows/release.yml` tự động chạy test, build `WinPilot.exe` + `WinPilot-win-x64.zip` và tạo **GitHub Release** mỗi khi push tag `v*.*.*`:

```powershell
git tag v0.1.0
git push origin v0.1.0
```
