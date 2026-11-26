using WinPilot.Core.Models;

namespace WinPilot.Core.Interfaces;

/// <summary>
/// Abstraction for notification channels so Telegram and future providers share one contract.
/// </summary>
public interface INotificationChannel
{
    string ChannelName { get; }
    Task<NotificationBatchResult> SendAsync(AppConfig config, string message, CancellationToken cancellationToken = default);
}
