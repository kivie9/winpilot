namespace WinPilot.Core.Models;

public enum LogLevel
{
    Info = 0,
    Success = 1,
    Warning = 2,
    Error = 3
}

public enum LogCategory
{
    System = 0,
    Launcher = 1,
    Telegram = 2,
    Shutdown = 3,
    Settings = 4,
    Session = 5
}

public sealed record LogEntry(
    DateTime Timestamp,
    LogLevel Level,
    LogCategory Category,
    string Message)
{
    public string FormattedTimestamp => Timestamp.ToString("HH:mm:ss");
    public string FormattedDateTime => Timestamp.ToString("dd/MM/yyyy HH:mm:ss");
    public string FormattedLine => $"[{Timestamp:HH:mm:ss}] [{Level,-7}] [{Category,-8}] {Message}";
}
