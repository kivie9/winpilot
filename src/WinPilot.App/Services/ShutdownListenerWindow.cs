using System.Runtime.InteropServices;
using System.Windows.Forms;
using Microsoft.Win32;
using WinPilot.Core.Models;
using WinPilot.Core.Services;

namespace WinPilot.App.Services;

/// <summary>
/// Always-alive hidden Win32 top-level window that intercepts WM_QUERYENDSESSION / WM_ENDSESSION
/// even when the main WPF dashboard is minimized to the system tray.
/// </summary>
public sealed class ShutdownListenerWindow : Form
{
    private const int WmQueryEndSession = 0x0011;
    private const int WmEndSession = 0x0016;
    private const long EndSessionLogoff = 0x80000000L;

    private readonly Func<AppConfig> _configProvider;
    private readonly AutomationOrchestrator _orchestrator;
    private readonly ActivityLogger _logger;

    public ShutdownListenerWindow(
        Func<AppConfig> configProvider,
        AutomationOrchestrator orchestrator,
        ActivityLogger logger)
    {
        _configProvider = configProvider;
        _orchestrator = orchestrator;
        _logger = logger;

        ShowInTaskbar = false;
        FormBorderStyle = FormBorderStyle.None;
        WindowState = FormWindowState.Minimized;
        Opacity = 0;
        Size = new System.Drawing.Size(0, 0);

        CreateHandle();

        // Request high shutdown priority (0x3FF) so Windows notifies us early while networking is still up.
        SetProcessShutdownParameters(0x3FF, 0);
        SystemEvents.SessionEnding += OnSystemSessionEnding;
        SystemEvents.PowerModeChanged += OnPowerModeChanged;
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            // ToolWindow avoids Alt+Tab visibility while remaining a real top-level HWND for WM_QUERYENDSESSION.
            cp.ExStyle |= 0x00000080; // WS_EX_TOOLWINDOW
            return cp;
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WmQueryEndSession)
        {
            var kind = IsLogoff(m.LParam) ? ShutdownEventKind.Logoff : ShutdownEventKind.ShutdownOrRestart;
            _logger.Info(LogCategory.Shutdown, $"Nhận tín hiệu WM_QUERYENDSESSION ({kind}).");

            ShutdownBlockReasonCreate(Handle, "WinPilot đang gửi thông báo Telegram trước khi tắt máy...");
            try
            {
                _orchestrator.ExecuteShutdownNotificationSync(_configProvider(), kind);
            }
            finally
            {
                ShutdownBlockReasonDestroy(Handle);
            }

            m.Result = new IntPtr(1); // TRUE: allow Windows shutdown to continue.
            return;
        }

        if (m.Msg == WmEndSession)
        {
            if (m.WParam == IntPtr.Zero)
            {
                // Shutdown was cancelled by the user or another application.
                ShutdownBlockReasonDestroy(Handle);
                _orchestrator.ResetShutdownGuard();
            }
            else
            {
                var kind = IsLogoff(m.LParam) ? ShutdownEventKind.Logoff : ShutdownEventKind.ShutdownOrRestart;
                _orchestrator.ExecuteShutdownNotificationSync(_configProvider(), kind);
            }

            m.Result = IntPtr.Zero;
            return;
        }

        base.WndProc(ref m);
    }

    private void OnSystemSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        var kind = e.Reason == SessionEndReasons.Logoff
            ? ShutdownEventKind.Logoff
            : ShutdownEventKind.ShutdownOrRestart;

        _orchestrator.ExecuteShutdownNotificationSync(_configProvider(), kind);
    }

    private void OnPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Suspend)
        {
            _orchestrator.ExecuteShutdownNotificationSync(_configProvider(), ShutdownEventKind.Sleep);
        }
        else if (e.Mode == PowerModes.Resume)
        {
            Task.Run(async () =>
            {
                await Task.Delay(2000);
                await _orchestrator.ExecuteWakeNotificationAsync(_configProvider());
            });
        }
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionLock)
        {
            Task.Run(() => _orchestrator.ExecuteShutdownNotificationSync(_configProvider(), ShutdownEventKind.Lock));
        }
        else if (e.Reason == SessionSwitchReason.SessionUnlock)
        {
            Task.Run(() => _orchestrator.ExecuteShutdownNotificationSync(_configProvider(), ShutdownEventKind.Unlock));
        }
    }

    private static bool IsLogoff(IntPtr lParam) => (lParam.ToInt64() & EndSessionLogoff) != 0;

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            SystemEvents.SessionEnding -= OnSystemSessionEnding;
            SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            SystemEvents.SessionSwitch -= OnSessionSwitch;
            ShutdownBlockReasonDestroy(Handle);
        }

        base.Dispose(disposing);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessShutdownParameters(uint dwLevel, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr hWnd, string pwszReason);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr hWnd);
}
