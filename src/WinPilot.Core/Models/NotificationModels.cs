namespace WinPilot.Core.Models;

public enum ShutdownEventKind
{
    ShutdownOrRestart = 0,
    Logoff = 1,
    Simulated = 2,
    Sleep = 3,
    Wake = 4,
    Lock = 5,
    Unlock = 6
}

public sealed record RecipientSendResult(
    string ChatId,
    bool Success,
    int? StatusCode,
    string Detail);

public sealed record NotificationBatchResult(
    bool AllSucceeded,
    bool HasAttempted,
    IReadOnlyList<RecipientSendResult> Recipients,
    string Summary);

public sealed record TelegramBotInfo(
    bool Success,
    string Username,
    string FirstName,
    string ErrorMessage);

public sealed record DiscoveredChat(
    string ChatId,
    string Title,
    string ChatType,
    string Username)
{
    public string DisplayLabel => string.IsNullOrWhiteSpace(Username)
        ? $"{Title} ({ChatType}) — {ChatId}"
        : $"{Title} (@{Username}, {ChatType}) — {ChatId}";
}

public enum LaunchOutcome
{
    Launched = 0,
    SkippedAlreadyRunning = 1,
    MissingFile = 2,
    Failed = 3
}

public sealed record AppLaunchItemResult(
    StartupAppEntry App,
    LaunchOutcome Outcome,
    string Detail);

public sealed record StartupLaunchSummary(
    int TotalEnabled,
    int LaunchedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<AppLaunchItemResult> Results);

public sealed record InstalledAppItem(
    string Name,
    string FilePath,
    string SourceLabel);
