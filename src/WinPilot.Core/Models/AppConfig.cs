using System.Text.Json.Serialization;

namespace WinPilot.Core.Models;

/// <summary>
/// Root persisted configuration for WinPilot.
/// </summary>
public sealed class AppConfig
{
    public int SchemaVersion { get; set; } = 2;

    public GeneralSettings General { get; set; } = new();

    public string ActiveProfileId { get; set; } = "default";

    public List<AppProfile> Profiles { get; set; } = new()
    {
        new AppProfile { Id = "default", Name = "Mặc định" }
    };

    public List<StartupAppEntry> StartupApps { get; set; } = new();

    public StartupConditions Conditions { get; set; } = new();

    public TelegramSettings Telegram { get; set; } = new();

    public List<CustomWebhookChannel> ExtraWebhooks { get; set; } = new();

    public VmwareSettings Vmware { get; set; } = new();

    public RuntimeStats Stats { get; set; } = new();
}

public sealed class AppProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Mặc định";
}

public sealed class StartupConditions
{
    public bool Enabled { get; set; } = false;

    /// <summary>
    /// Comma-separated allowed Wi-Fi SSIDs (empty = any network).
    /// </summary>
    public string AllowedWifiSsids { get; set; } = string.Empty;

    /// <summary>
    /// Allowed days of week (0 = Sunday .. 6 = Saturday). Empty = all days.
    /// </summary>
    public List<int> AllowedDays { get; set; } = new() { 1, 2, 3, 4, 5 };
}

public sealed class GeneralSettings
{
    public bool AutoStartWithWindows { get; set; } = true;
    public bool StartMinimizedToTray { get; set; } = true;
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public int StartupDelaySeconds { get; set; } = 3;
    public int NetworkWaitTimeoutSeconds { get; set; } = 60;
    public bool RunStartupTasksOnlyWithStartupArg { get; set; } = true;
}

public enum AppWindowMode
{
    Normal = 0,
    Minimized = 1,
    Maximized = 2,
    Hidden = 3
}

public sealed class StartupAppEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string ProfileId { get; set; } = "default";
    public string Name { get; set; } = string.Empty;
    public string Path { get; set; } = string.Empty;
    public string Arguments { get; set; } = string.Empty;
    public string WorkingDirectory { get; set; } = string.Empty;
    public int DelaySeconds { get; set; }
    public AppWindowMode WindowMode { get; set; } = AppWindowMode.Normal;
    public bool RunAsAdmin { get; set; }
    public bool SkipIfAlreadyRunning { get; set; } = true;
    public bool Enabled { get; set; } = true;
    public int Order { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public enum StartupNotificationMode
{
    ParallelImmediate = 0,
    AfterAppsLaunched = 1
}

public sealed class TelegramSettings
{
    public bool Enabled { get; set; } = true;

    public string EncryptedBotToken { get; set; } = string.Empty;

    [JsonIgnore]
    public string BotToken { get; set; } = string.Empty;

    public string ChatIds { get; set; } = string.Empty;

    public string ParseMode { get; set; } = "HTML";

    public bool NotifyOnStartup { get; set; } = true;

    public StartupNotificationMode StartupSendMode { get; set; } = StartupNotificationMode.ParallelImmediate;

    public string StartupTemplate { get; set; } =
        "🖥️ <b>THÔNG BÁO KHỞI ĐỘNG</b>\n" +
        "👤 Thiết bị: <code>{pc_name}</code> ({user})\n" +
        "🕒 Lúc: <b>{time}</b> - {date}\n" +
        "🌐 IP nội bộ: <code>{local_ip}</code>\n" +
        "🔗 IP Tailscale: <code>{tailscale_ip}</code>\n" +
        "🚀 Ứng dụng tự mở ({apps_count}):\n{apps_list}";

    public bool NotifyOnShutdown { get; set; } = true;
    public bool NotifyOnLogoff { get; set; } = true;
    public bool NotifyOnSleep { get; set; } = false;
    public bool NotifyOnWake { get; set; } = false;
    public bool NotifyOnLock { get; set; } = false;
    public bool NotifyOnUnlock { get; set; } = false;

    public int ShutdownTimeoutMs { get; set; } = 3500;

    public string ShutdownTemplate { get; set; } =
        "🔴 <b>CẢNH BÁO: {event}</b>\n" +
        "👤 Thiết bị: <code>{pc_name}</code> ({user})\n" +
        "🕒 Lúc: <b>{time}</b> - {date}\n" +
        "🔗 IP Tailscale: <code>{tailscale_ip}</code>\n" +
        "⏱ Thời gian hoạt động: <code>{uptime}</code>";

    public bool EnableRemoteCommands { get; set; } = false;
    public string AllowedCommandChatIds { get; set; } = string.Empty;
}

public enum WebhookChannelType
{
    CustomJson = 0,
    Discord = 1,
    Slack = 2
}

public sealed class CustomWebhookChannel
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public WebhookChannelType ChannelType { get; set; } = WebhookChannelType.Discord;
    public string Url { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool NotifyOnStartup { get; set; } = true;
    public bool NotifyOnShutdown { get; set; } = true;
}

public sealed class VmwareSettings
{
    public string VmrunPath { get; set; } = string.Empty;
    public bool AutoStartEnabled { get; set; } = true;
    public bool StopVmsOnShutdown { get; set; } = false;
    public List<VmwareVmEntry> Vms { get; set; } = new();
}

public sealed class VmwareVmEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = string.Empty;
    public string VmxPath { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool Headless { get; set; } = true;
    public int DelaySeconds { get; set; }
    public int Order { get; set; }
    public string Notes { get; set; } = string.Empty;
}

public sealed class RuntimeStats
{
    public DateTimeOffset? LastStartupRunAt { get; set; }
    public string LastStartupSummary { get; set; } = "Chưa chạy lần nào";
    public DateTimeOffset? LastTelegramSentAt { get; set; }
    public string LastTelegramStatus { get; set; } = "Chưa gửi";
    public DateTimeOffset? LastShutdownSentAt { get; set; }
}
