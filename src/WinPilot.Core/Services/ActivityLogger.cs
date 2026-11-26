using System.Text;
using WinPilot.Core.Models;

namespace WinPilot.Core.Services;

/// <summary>
/// Thread-safe activity logger writing daily log files and notifying live UI subscribers.
/// </summary>
public sealed class ActivityLogger
{
    private const int MaxInMemoryEntries = 500;
    private readonly object _sync = new();
    private readonly List<LogEntry> _entries = new();
    private readonly string _logDirectory;

    public event Action<LogEntry>? EntryAdded;
    public event Action? Cleared;

    public ActivityLogger(string? logDirectory = null)
    {
        _logDirectory = logDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinPilot",
            "logs");
    }

    public string LogDirectory => _logDirectory;

    public IReadOnlyList<LogEntry> GetSnapshot()
    {
        lock (_sync)
        {
            return _entries.ToList();
        }
    }

    public void Info(LogCategory category, string message) => Log(LogLevel.Info, category, message);
    public void Success(LogCategory category, string message) => Log(LogLevel.Success, category, message);
    public void Warning(LogCategory category, string message) => Log(LogLevel.Warning, category, message);
    public void Error(LogCategory category, string message) => Log(LogLevel.Error, category, message);

    public void Log(LogLevel level, LogCategory category, string message)
    {
        var entry = new LogEntry(DateTime.Now, level, category, message);

        lock (_sync)
        {
            _entries.Add(entry);
            if (_entries.Count > MaxInMemoryEntries)
            {
                _entries.RemoveAt(0);
            }

            try
            {
                Directory.CreateDirectory(_logDirectory);
                string filePath = Path.Combine(_logDirectory, $"winpilot-{entry.Timestamp:yyyy-MM-dd}.log");
                File.AppendAllText(filePath, entry.FormattedLine + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging to disk must never crash startup/shutdown flows.
            }
        }

        EntryAdded?.Invoke(entry);
    }

    public void LoadTodayLogsFromDisk()
    {
        try
        {
            string filePath = Path.Combine(_logDirectory, $"winpilot-{DateTime.Now:yyyy-MM-dd}.log");
            if (!File.Exists(filePath)) return;

            var lines = File.ReadAllLines(filePath, Encoding.UTF8);
            lock (_sync)
            {
                _entries.Clear();
                foreach (string line in lines.TakeLast(MaxInMemoryEntries))
                {
                    if (TryParseLine(line, out var parsed))
                    {
                        _entries.Add(parsed);
                    }
                }
            }
        }
        catch
        {
            // Ignore malformed disk log lines.
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
        }

        Cleared?.Invoke();
    }

    private static bool TryParseLine(string line, out LogEntry entry)
    {
        entry = default!;
        if (string.IsNullOrWhiteSpace(line) || line.Length < 34 || line[0] != '[') return false;

        try
        {
            string timePart = line.Substring(1, 8);
            string levelPart = line.Substring(12, 7).Trim();
            string catPart = line.Substring(22, 8).Trim();
            string msg = line.Length > 32 ? line[32..] : string.Empty;

            if (!TimeSpan.TryParse(timePart, out var tod)) return false;
            if (!Enum.TryParse<LogLevel>(levelPart, true, out var level)) level = LogLevel.Info;
            if (!Enum.TryParse<LogCategory>(catPart, true, out var cat)) cat = LogCategory.System;

            entry = new LogEntry(DateTime.Today.Add(tod), level, cat, msg);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
