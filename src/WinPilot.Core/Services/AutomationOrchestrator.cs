using System.Net.NetworkInformation;
using WinPilot.Core.Models;

namespace WinPilot.Core.Services;

/// <summary>
/// Coordinates startup app launching, condition checks, parallel/shutdown Telegram & webhook notifications.
/// </summary>
public sealed class AutomationOrchestrator
{
    private readonly ConfigService _configService;
    private readonly AppLauncherService _launcher;
    private readonly TelegramNotificationService _telegram;
    private readonly ActivityLogger _logger;
    private readonly WebhookNotificationService _webhooks;
    private readonly ConditionEvaluator _conditions;
    private readonly VmwareService _vmware;
    private int _shutdownGuard;

    public string LastStartupStatus { get; private set; } = "Chưa chạy phiên nào";
    public DateTime? LastStartupRunAt { get; private set; }
    public string LastShutdownStatus { get; private set; } = "Đang lắng nghe sự kiện tắt máy";

    public event Action? StateChanged;

    public AutomationOrchestrator(
        ConfigService configService,
        AppLauncherService launcher,
        TelegramNotificationService telegram,
        ActivityLogger logger,
        WebhookNotificationService? webhooks = null,
        ConditionEvaluator? conditions = null,
        VmwareService? vmware = null)
    {
        _configService = configService;
        _launcher = launcher;
        _telegram = telegram;
        _logger = logger;
        _webhooks = webhooks ?? new WebhookNotificationService(logger: logger);
        _conditions = conditions ?? new ConditionEvaluator();
        _vmware = vmware ?? new VmwareService(logger);
    }

    public async Task RunStartupWorkflowAsync(
        AppConfig config,
        bool isBootLaunch,
        string? profileIdOverride = null,
        CancellationToken cancellationToken = default)
    {
        string targetProfileId = profileIdOverride ?? config.ActiveProfileId;
        string profileName = config.Profiles.FirstOrDefault(p => p.Id == targetProfileId)?.Name ?? "Mặc định";

        _logger.Info(LogCategory.System, isBootLaunch
            ? $"WinPilot khởi động cùng Windows (Profile: {profileName}) — bắt đầu quy trình tự động."
            : $"Đang chạy quy trình khởi động (Profile: {profileName}).");

        if (isBootLaunch && config.General.StartupDelaySeconds > 0)
        {
            _logger.Info(LogCategory.System, $"Đợi {config.General.StartupDelaySeconds}s để hệ thống ổn định...");
            await Task.Delay(TimeSpan.FromSeconds(config.General.StartupDelaySeconds), cancellationToken)
                .ConfigureAwait(false);
        }

        if (isBootLaunch)
        {
            var cond = _conditions.Evaluate(config.Conditions);
            if (!cond.Allowed)
            {
                _logger.Warning(LogCategory.System, $"Bỏ qua mở ứng dụng do điều kiện chưa đạt: {cond.Reason}");
                LastStartupStatus = $"Bỏ qua: {cond.Reason}";
                StateChanged?.Invoke();
                return;
            }
        }

        var profileApps = config.StartupApps
            .Where(a => a.ProfileId == targetProfileId)
            .OrderBy(a => a.Order)
            .ToList();

        var enabledApps = profileApps.Where(a => a.Enabled).ToList();
        var enabledVms = config.Vmware.AutoStartEnabled
            ? config.Vmware.Vms.Where(v => v.Enabled).OrderBy(v => v.Order).ToList()
            : new List<VmwareVmEntry>();

        StartupLaunchSummary launchSummary;
        VmBatchSummary vmSummary;
        NotificationBatchResult? notifyResult = null;

        bool shouldSendTelegram = config.Telegram.Enabled && config.Telegram.NotifyOnStartup;

        if (shouldSendTelegram && config.Telegram.StartupSendMode == StartupNotificationMode.ParallelImmediate)
        {
            var initialNames = enabledApps.Select(a => a.Name)
                .Concat(enabledVms.Select(v => $"[VM] {v.Name}"))
                .ToList();

            var launchTask = _launcher.LaunchEnabledAppsAsync(profileApps, cancellationToken);
            var vmTask = _vmware.StartEnabledVmsAsync(config.Vmware, cancellationToken);
            var notifyTask = SendStartupTelegramAsync(config, initialNames, cancellationToken);

            await Task.WhenAll(launchTask, vmTask, notifyTask).ConfigureAwait(false);
            launchSummary = await launchTask.ConfigureAwait(false);
            vmSummary = await vmTask.ConfigureAwait(false);
            notifyResult = await notifyTask.ConfigureAwait(false);
        }
        else
        {
            var launchTask = _launcher.LaunchEnabledAppsAsync(profileApps, cancellationToken);
            var vmTask = _vmware.StartEnabledVmsAsync(config.Vmware, cancellationToken);
            await Task.WhenAll(launchTask, vmTask).ConfigureAwait(false);

            launchSummary = await launchTask.ConfigureAwait(false);
            vmSummary = await vmTask.ConfigureAwait(false);

            if (shouldSendTelegram)
            {
                var launchedNames = launchSummary.Results
                    .Where(r => r.Outcome is LaunchOutcome.Launched or LaunchOutcome.SkippedAlreadyRunning)
                    .Select(r => $"{r.App.Name} ({(r.Outcome == LaunchOutcome.Launched ? "Đã mở" : "Đang chạy")})")
                    .Concat(vmSummary.Results
                        .Where(r => r.Outcome is VmLaunchOutcome.Started or VmLaunchOutcome.SkippedAlreadyRunning)
                        .Select(r => $"[VM] {r.Vm.Name} ({(r.Outcome == VmLaunchOutcome.Started ? "Đã bật" : "Đang chạy")})"))
                    .ToList();
                notifyResult = await SendStartupTelegramAsync(config, launchedNames, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        LastStartupRunAt = DateTime.Now;
        string appPart = $"Mở {launchSummary.LaunchedCount} app" +
            (launchSummary.SkippedCount > 0 ? $", bỏ qua {launchSummary.SkippedCount}" : "") +
            (launchSummary.FailedCount > 0 ? $", lỗi {launchSummary.FailedCount}" : "");

        string vmPart = (vmSummary.StartedCount + vmSummary.SkippedCount + vmSummary.FailedCount) > 0
            ? $" • VM: {vmSummary.StartedCount} bật" +
              (vmSummary.SkippedCount > 0 ? $", {vmSummary.SkippedCount} đang chạy" : "") +
              (vmSummary.FailedCount > 0 ? $", {vmSummary.FailedCount} lỗi" : "")
            : "";

        string tgPart = notifyResult is null
            ? "Telegram tắt"
            : notifyResult.AllSucceeded ? "Telegram OK" : "Telegram lỗi";

        LastStartupStatus = $"{appPart}{vmPart} • {tgPart} ({LastStartupRunAt:HH:mm:ss})";
        config.Stats.LastStartupRunAt = LastStartupRunAt;
        config.Stats.LastStartupSummary = LastStartupStatus;
        TryPersistStats(config);

        _logger.Info(LogCategory.System, $"Hoàn tất quy trình khởi động: {LastStartupStatus}");
        StateChanged?.Invoke();
    }

    public Task RunStartupWorkflowAsync(AppConfig config, bool isBootLaunch, CancellationToken cancellationToken) =>
        RunStartupWorkflowAsync(config, isBootLaunch, null, cancellationToken);

    public async Task<NotificationBatchResult> SendStartupTelegramAsync(
        AppConfig config,
        IReadOnlyList<string> appNames,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset bootTime = DateTimeOffset.Now;
        string RenderNow() => TemplateRenderer.Render(
            config.Telegram.StartupTemplate,
            new TemplateContext
            {
                Now = bootTime,
                EventLabel = "Khởi động máy",
                AppNames = appNames
            },
            config.Telegram.ParseMode);

        var result = await _telegram.SendWithNetworkRetryAsync(
            config.Telegram.BotToken,
            config.Telegram.ChatIds,
            RenderNow,
            config.Telegram.ParseMode,
            config.General.NetworkWaitTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);

        string renderedFinal = RenderNow();
        await _webhooks.SendToAllAsync(
            config.ExtraWebhooks,
            isStartup: true,
            "Khởi động máy",
            renderedFinal,
            appNames,
            cancellationToken).ConfigureAwait(false);

        config.Stats.LastTelegramSentAt = DateTimeOffset.Now;
        config.Stats.LastTelegramStatus = result.Summary;
        TryPersistStats(config);

        if (result.AllSucceeded)
        {
            _logger.Success(LogCategory.Telegram, $"[Khởi động] {result.Summary}");
        }
        else
        {
            _logger.Warning(LogCategory.Telegram, $"[Khởi động] {result.Summary}");
            bool isTransient = result.HasAttempted && result.Recipients.Any(r => r.StatusCode is null or >= 500 or 429);
            if (isTransient && !cancellationToken.IsCancellationRequested)
            {
                ScheduleOfflineRecovery(config, RenderNow, appNames, "Khởi động (gửi bù khi có mạng)", cancellationToken);
            }
        }

        return result;
    }

    public async Task<NotificationBatchResult?> ExecuteWakeNotificationAsync(
        AppConfig config,
        CancellationToken cancellationToken = default)
    {
        if (!config.Telegram.Enabled || !config.Telegram.NotifyOnWake)
        {
            return null;
        }

        DateTimeOffset wakeTime = DateTimeOffset.Now;
        var appNames = config.StartupApps
            .Where(a => a.ProfileId == config.ActiveProfileId && a.Enabled)
            .Select(a => a.Name)
            .ToList();

        string RenderWake() => TemplateRenderer.Render(
            config.Telegram.ShutdownTemplate,
            new TemplateContext
            {
                Now = wakeTime,
                EventLabel = "Máy tính vừa mở lại từ Sleep",
                AppNames = appNames
            },
            config.Telegram.ParseMode);

        var result = await _telegram.SendWithNetworkRetryAsync(
            config.Telegram.BotToken,
            config.Telegram.ChatIds,
            RenderWake,
            config.Telegram.ParseMode,
            config.General.NetworkWaitTimeoutSeconds,
            cancellationToken).ConfigureAwait(false);

        if (result.AllSucceeded)
        {
            _logger.Success(LogCategory.Session, $"[Wake] {result.Summary}");
        }
        else
        {
            _logger.Warning(LogCategory.Session, $"[Wake] {result.Summary}");
        }

        StateChanged?.Invoke();
        return result;
    }

    private void ScheduleOfflineRecovery(
        AppConfig config,
        Func<string> messageFactory,
        IReadOnlyList<string> appNames,
        string label,
        CancellationToken cancellationToken)
    {
        _logger.Info(LogCategory.Telegram, $"[{label}] Đưa vào hàng đợi — sẽ tự gửi ngay khi máy có kết nối Internet.");
        _ = Task.Run(async () =>
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                void OnNetChanged(object? s, NetworkAvailabilityEventArgs e)
                {
                    if (e.IsAvailable) tcs.TrySetResult(true);
                }

                NetworkChange.NetworkAvailabilityChanged += OnNetChanged;
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    var waitTask = Task.Delay(TimeSpan.FromSeconds(30), linked.Token);
                    await Task.WhenAny(waitTask, tcs.Task).ConfigureAwait(false);
                    linked.Cancel();
                }
                finally
                {
                    NetworkChange.NetworkAvailabilityChanged -= OnNetChanged;
                }

                if (cancellationToken.IsCancellationRequested) break;
                if (!NetworkInterface.GetIsNetworkAvailable()) continue;

                string msg = messageFactory();
                var retry = await _telegram.SendMessageAsync(
                    config.Telegram.BotToken,
                    config.Telegram.ChatIds,
                    msg,
                    config.Telegram.ParseMode,
                    cancellationToken).ConfigureAwait(false);

                if (retry.AllSucceeded)
                {
                    await _webhooks.SendToAllAsync(config.ExtraWebhooks, true, label, msg, appNames, cancellationToken)
                        .ConfigureAwait(false);
                    _logger.Success(LogCategory.Telegram, $"[{label}] Đã gửi bù thành công sau khi có mạng.");
                    config.Stats.LastTelegramSentAt = DateTimeOffset.Now;
                    config.Stats.LastTelegramStatus = retry.Summary;
                    TryPersistStats(config);
                    StateChanged?.Invoke();
                    break;
                }

                bool permanent = retry.Recipients.All(r => r.StatusCode is >= 400 and < 500 && r.StatusCode != 429);
                if (permanent) break;
            }
        }, cancellationToken);
    }

    public void ResetShutdownGuard()
    {
        Interlocked.Exchange(ref _shutdownGuard, 0);
        LastShutdownStatus = "Đã hủy tắt máy — tiếp tục lắng nghe";
        _logger.Info(LogCategory.Shutdown, "Sự kiện tắt máy bị hủy; đặt lại bộ lắng nghe.");
        StateChanged?.Invoke();
    }

    public NotificationBatchResult? ExecuteShutdownNotificationSync(AppConfig config, ShutdownEventKind kind)
    {
        bool isShutdownSequence = kind is ShutdownEventKind.ShutdownOrRestart or ShutdownEventKind.Logoff;
        if (isShutdownSequence && Interlocked.Exchange(ref _shutdownGuard, 1) == 1)
        {
            return null;
        }

        if (isShutdownSequence && config.Vmware.StopVmsOnShutdown)
        {
            _vmware.StopManagedVmsOnShutdown(config.Vmware);
        }

        if (!config.Telegram.Enabled) return null;

        bool enabledForKind = kind switch
        {
            ShutdownEventKind.ShutdownOrRestart => config.Telegram.NotifyOnShutdown,
            ShutdownEventKind.Simulated => config.Telegram.NotifyOnShutdown,
            ShutdownEventKind.Logoff => config.Telegram.NotifyOnShutdown && config.Telegram.NotifyOnLogoff,
            ShutdownEventKind.Sleep => config.Telegram.NotifyOnSleep,
            ShutdownEventKind.Wake => config.Telegram.NotifyOnWake,
            ShutdownEventKind.Lock => config.Telegram.NotifyOnLock,
            ShutdownEventKind.Unlock => config.Telegram.NotifyOnUnlock,
            _ => false
        };

        if (!enabledForKind) return null;

        string eventLabel = kind switch
        {
            ShutdownEventKind.Logoff => "Đăng xuất Windows",
            ShutdownEventKind.Simulated => "Giả lập tắt máy (Test)",
            ShutdownEventKind.Sleep => "Máy tính đi vào chế độ Ngủ (Sleep)",
            ShutdownEventKind.Wake => "Máy tính vừa mở lại từ Sleep",
            ShutdownEventKind.Lock => "Màn hình đã bị khóa (Lock)",
            ShutdownEventKind.Unlock => "Đã mở khóa màn hình (Unlock)",
            _ => "Tắt máy / Khởi động lại"
        };

        var appNames = config.StartupApps
            .Where(a => a.ProfileId == config.ActiveProfileId && a.Enabled)
            .Select(a => a.Name)
            .ToList();

        var context = new TemplateContext
        {
            EventLabel = eventLabel,
            AppNames = appNames
        };

        string message = TemplateRenderer.Render(
            config.Telegram.ShutdownTemplate,
            context,
            config.Telegram.ParseMode);

        LogCategory logCategory = kind is ShutdownEventKind.Sleep or ShutdownEventKind.Wake or ShutdownEventKind.Lock or ShutdownEventKind.Unlock
            ? LogCategory.Session
            : LogCategory.Shutdown;

        int timeoutMs = Math.Clamp(config.Telegram.ShutdownTimeoutMs, 1000, 4500);
        using var cts = new CancellationTokenSource(timeoutMs);

        try
        {
            var result = Task.Run(async () =>
            {
                var tgTask = _telegram.SendMessageAsync(
                    config.Telegram.BotToken,
                    config.Telegram.ChatIds,
                    message,
                    config.Telegram.ParseMode,
                    cts.Token);

                var whTask = _webhooks.SendToAllAsync(
                    config.ExtraWebhooks,
                    isStartup: false,
                    eventLabel,
                    message,
                    appNames,
                    cts.Token);

                await Task.WhenAll(tgTask, whTask).ConfigureAwait(false);
                return await tgTask.ConfigureAwait(false);
            }, cts.Token).GetAwaiter().GetResult();

            if (result.AllSucceeded)
            {
                LastShutdownStatus = $"Đã gửi ({eventLabel}) lúc {DateTime.Now:HH:mm:ss}";
                config.Stats.LastShutdownSentAt = DateTimeOffset.Now;
                config.Stats.LastTelegramSentAt = DateTimeOffset.Now;
                config.Stats.LastTelegramStatus = result.Summary;
                TryPersistStats(config);
                _logger.Success(logCategory, $"[{eventLabel}] {result.Summary}");
            }
            else
            {
                LastShutdownStatus = $"Lỗi gửi ({eventLabel}): {result.Summary}";
                _logger.Warning(logCategory, $"[{eventLabel}] {result.Summary}");
            }

            StateChanged?.Invoke();
            return result;
        }
        catch (Exception ex)
        {
            LastShutdownStatus = $"Timeout/Lỗi ({eventLabel}): {ex.Message}";
            _logger.Error(logCategory, $"[{eventLabel}] Thất bại: {ex.Message}");
            StateChanged?.Invoke();
            return new NotificationBatchResult(false, false, Array.Empty<RecipientSendResult>(), ex.Message);
        }
    }

    private void TryPersistStats(AppConfig config)
    {
        try { _configService.Save(config); }
        catch { /* Non-fatal */ }
    }
}
