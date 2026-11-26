using System.Threading;
using System.Windows;
using WinPilot.App.Services;
using WinPilot.Core.Models;
using WinPilot.Core.Services;
using Application = System.Windows.Application;

namespace WinPilot.App;

public partial class App : Application
{
    private const string MutexName = @"Local\WinPilot_SingleInstance_Mutex";
    private const string ActivateEventName = @"Local\WinPilot_ActivateWindow_Event";

    private Mutex? _singleInstanceMutex;
    private EventWaitHandle? _activateEvent;
    private CancellationTokenSource? _appCts;

    private ConfigService _configService = null!;
    private ActivityLogger _logger = null!;
    private WindowsStartupManager _startupManager = null!;
    private AppLauncherService _launcher = null!;
    private TelegramNotificationService _telegram = null!;
    private WebhookNotificationService _webhooks = null!;
    private VmwareService _vmware = null!;
    private AutomationOrchestrator _orchestrator = null!;
    private TelegramCommandListener _commandListener = null!;

    private TrayIconService? _trayService;
    private ShutdownListenerWindow? _shutdownListener;
    private MainWindow? _mainWindow;
    private AppConfig _config = null!;

    public bool IsShuttingDownExplicitly { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);
        if (!createdNew)
        {
            // Signal the existing tray instance to bring its window to the foreground.
            try
            {
                using var evt = EventWaitHandle.OpenExisting(ActivateEventName);
                evt.Set();
            }
            catch
            {
                // Ignore if already exiting.
            }

            Shutdown();
            return;
        }

        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        _appCts = new CancellationTokenSource();

        _configService = new ConfigService();
        _logger = new ActivityLogger();
        _logger.LoadTodayLogsFromDisk();
        _startupManager = new WindowsStartupManager();
        _launcher = new AppLauncherService(_logger);
        _telegram = new TelegramNotificationService(logger: _logger);
        _webhooks = new WebhookNotificationService(logger: _logger);
        _vmware = new VmwareService(_logger);
        _orchestrator = new AutomationOrchestrator(_configService, _launcher, _telegram, _logger, _webhooks, vmware: _vmware);
        _commandListener = new TelegramCommandListener(() => _config, _orchestrator, _logger);

        _config = _configService.Load();
        _commandListener.SyncState();

        // Keep Registry startup path synchronized if enabled.
        try
        {
            if (_config.General.AutoStartWithWindows && Environment.ProcessPath is string exePath)
            {
                _startupManager.SetEnabled(true, exePath);
            }
        }
        catch (Exception ex)
        {
            _logger.Warning(LogCategory.Settings, $"Không thể đồng bộ Registry Startup: {ex.Message}");
        }

        _shutdownListener = new ShutdownListenerWindow(() => _config, _orchestrator, _logger);

        _trayService = new TrayIconService();
        _trayService.UpdateState(_config);
        _trayService.OpenDashboardRequested += ShowMainWindow;
        _trayService.RunStartupAppsRequested += profileId => _ = RunStartupNowAsync(isBoot: false, profileId);
        _trayService.TestStartupTelegramRequested += () => _ = TestStartupTelegramAsync();
        _trayService.TestShutdownTelegramRequested += () => _ = Task.Run(() => _orchestrator.ExecuteShutdownNotificationSync(_config, ShutdownEventKind.Simulated));
        _trayService.ExitRequested += ExitApplication;

        _orchestrator.StateChanged += () => Dispatcher.InvokeAsync(() => _trayService?.UpdateState(_config));

        StartActivationListener(_appCts.Token);

        bool isStartupLaunch = e.Args.Any(a =>
            string.Equals(a, WindowsStartupManager.StartupArgument, StringComparison.OrdinalIgnoreCase));

        _mainWindow = new MainWindow(
            _config,
            _configService,
            _logger,
            _startupManager,
            _launcher,
            _telegram,
            _webhooks,
            _vmware,
            _orchestrator,
            _commandListener,
            () => _trayService?.UpdateState(_config),
            () => IsShuttingDownExplicitly);

        if (isStartupLaunch && _config.General.StartMinimizedToTray)
        {
            _logger.Info(LogCategory.System, "WinPilot khởi động ở chế độ nền (System Tray).");
        }
        else
        {
            ShowMainWindow();
        }

        if (isStartupLaunch || !_config.General.RunStartupTasksOnlyWithStartupArg)
        {
            _ = RunStartupNowAsync(isStartupLaunch);
        }
    }

    private void StartActivationListener(CancellationToken token)
    {
        _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        Task.Run(() =>
        {
            while (!token.IsCancellationRequested)
            {
                int signaled = WaitHandle.WaitAny(new[] { _activateEvent, token.WaitHandle });
                if (signaled == 0)
                {
                    Dispatcher.Invoke(ShowMainWindow);
                }
                else
                {
                    break;
                }
            }
        }, token);
    }

    private void ShowMainWindow()
    {
        if (_mainWindow is null) return;

        if (!_mainWindow.IsVisible)
        {
            _mainWindow.Show();
        }

        if (_mainWindow.WindowState == WindowState.Minimized)
        {
            _mainWindow.WindowState = WindowState.Normal;
        }

        _mainWindow.Activate();
        _mainWindow.Topmost = true;
        _mainWindow.Topmost = false;
        _mainWindow.Focus();
    }

    private async Task RunStartupNowAsync(bool isBoot, string? profileId = null)
    {
        try
        {
            await _orchestrator.RunStartupWorkflowAsync(_config, isBoot, profileId, _appCts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory.System, $"Lỗi chạy quy trình khởi động: {ex.Message}");
        }
    }

    private async Task TestStartupTelegramAsync()
    {
        var enabledNames = _config.StartupApps
            .Where(a => a.ProfileId == _config.ActiveProfileId && a.Enabled)
            .Select(a => a.Name)
            .ToList();
        var result = await _orchestrator.SendStartupTelegramAsync(_config, enabledNames);
        _trayService?.ShowBalloon(
            result.AllSucceeded ? "Telegram thành công" : "Telegram có lỗi",
            result.Summary,
            result.AllSucceeded ? System.Windows.Forms.ToolTipIcon.Info : System.Windows.Forms.ToolTipIcon.Warning);
    }

    private void ExitApplication()
    {
        IsShuttingDownExplicitly = true;
        _appCts?.Cancel();
        _commandListener.Dispose();
        _trayService?.Dispose();
        _shutdownListener?.Dispose();
        _activateEvent?.Dispose();
        _singleInstanceMutex?.ReleaseMutex();
        _singleInstanceMutex?.Dispose();
        Shutdown();
    }
}
