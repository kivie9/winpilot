# Changelog

## [v0.1.0] - 2026-11-26

### Tính năng & Cải tiến
- **Khởi động & Quản lý Profile:** Tự động mở ứng dụng theo profile, hỗ trợ kéo-thả, quét Start Menu, điều kiện ngày/Wi-Fi SSID và chạy dưới System Tray.
- **Thông báo hai đầu & Điều khiển từ xa:** Tích hợp Telegram Bot (hỗ trợ đa Chat ID, nhận lệnh từ xa, tự động gửi bù khi có mạng) và Webhook.
- **Giám sát nguồn & phiên Windows:** Hỗ trợ `Shutdown`, `Restart`, `Logoff`, `Sleep`, `Wake`, `Lock`, `Unlock`.
- **Icon đa độ phân giải:** Thiết kế lại `app.ico` với 7 kích thước sắc nét (`16×16` đến `256×256`), tối ưu hiển thị trên Taskbar, Explorer và System Tray.
- **Phân loại nhật ký rõ ràng:** Tách riêng nhóm log `Session` (`Sleep`/`Wake`/`Lock`/`Unlock`) khỏi `Shutdown`.
- **Tự động phát hành:** Thêm GitHub Actions workflow `.github/workflows/release.yml` tự động test, build và tạo Release kèm `WinPilot.exe` & `WinPilot-win-x64.zip`.
