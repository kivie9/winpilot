using System.Drawing;
using System.IO;
using System.Windows.Forms;
using WinPilot.Core.Models;

namespace WinPilot.App.Services;

/// <summary>
/// Manages the Windows System Tray (NotifyIcon) and dynamic profile context menu.
/// </summary>
public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _profilesMenu;

    public event Action? OpenDashboardRequested;
    public event Action<string?>? RunStartupAppsRequested;
    public event Action? TestStartupTelegramRequested;
    public event Action? TestShutdownTelegramRequested;
    public event Action? ExitRequested;

    public TrayIconService()
    {
        Icon icon = LoadAppIcon();
        var menu = new ContextMenuStrip();

        var openItem = new ToolStripMenuItem("Mở bảng điều khiển WinPilot")
        {
            Font = new Font(menu.Font, FontStyle.Bold)
        };
        openItem.Click += (_, _) => OpenDashboardRequested?.Invoke();

        var runAppsItem = new ToolStripMenuItem("Chạy Profile hiện tại ngay");
        runAppsItem.Click += (_, _) => RunStartupAppsRequested?.Invoke(null);

        _profilesMenu = new ToolStripMenuItem("Chạy Profile khác");

        var testStartItem = new ToolStripMenuItem("Test gửi Telegram (Khởi động)");
        testStartItem.Click += (_, _) => TestStartupTelegramRequested?.Invoke();

        var testStopItem = new ToolStripMenuItem("Test gửi Telegram (Tắt máy)");
        testStopItem.Click += (_, _) => TestShutdownTelegramRequested?.Invoke();

        var exitItem = new ToolStripMenuItem("Thoát hoàn toàn");
        exitItem.Click += (_, _) => ExitRequested?.Invoke();

        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(runAppsItem);
        menu.Items.Add(_profilesMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(testStartItem);
        menu.Items.Add(testStopItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Icon = icon,
            Text = "WinPilot — Control Center",
            ContextMenuStrip = menu,
            Visible = true
        };

        _notifyIcon.DoubleClick += (_, _) => OpenDashboardRequested?.Invoke();
    }

    public void UpdateState(AppConfig config)
    {
        _profilesMenu.DropDownItems.Clear();
        foreach (var profile in config.Profiles)
        {
            string label = profile.Id == config.ActiveProfileId ? $"✓ {profile.Name}" : profile.Name;
            var item = new ToolStripMenuItem(label);
            string pid = profile.Id;
            item.Click += (_, _) => RunStartupAppsRequested?.Invoke(pid);
            _profilesMenu.DropDownItems.Add(item);
        }

        bool tgReady = config.Telegram.Enabled && !string.IsNullOrWhiteSpace(config.Telegram.BotToken);
        string status = tgReady ? "Sẵn sàng" : "Chưa cấu hình Telegram";
        _notifyIcon.Text = $"WinPilot — {status}".Length > 63
            ? "WinPilot — Control Center"
            : $"WinPilot — {status}";
    }

    public void ShowBalloon(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon.ShowBalloonTip(3000, title, text, icon);
    }

    private static Icon LoadAppIcon()
    {
        try
        {
            var assembly = typeof(TrayIconService).Assembly;
            string? resourceName = assembly.GetManifestResourceNames()
                .FirstOrDefault(n => n.EndsWith("app.ico", StringComparison.OrdinalIgnoreCase));
            if (resourceName is not null)
            {
                using Stream? stream = assembly.GetManifestResourceStream(resourceName);
                if (stream is not null)
                {
                    return new Icon(stream, SystemInformation.SmallIconSize);
                }
            }

            string? exePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exePath) && File.Exists(exePath))
            {
                Icon? extracted = Icon.ExtractAssociatedIcon(exePath);
                if (extracted is not null) return extracted;
            }
        }
        catch { /* Fallback below */ }

        return SystemIcons.Application;
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
