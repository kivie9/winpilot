using System.Diagnostics;
using System.Runtime.InteropServices;
using WinPilot.Core.Models;

namespace WinPilot.Core.Services;

/// <summary>
/// Validates and launches configured local Windows apps, shortcuts, scripts, and URIs.
/// </summary>
public sealed class AppLauncherService
{
    private readonly ActivityLogger _logger;
    private readonly Func<string, bool> _isProcessRunning;

    public AppLauncherService(ActivityLogger logger, Func<string, bool>? isProcessRunning = null)
    {
        _logger = logger;
        _isProcessRunning = isProcessRunning ?? DefaultIsProcessRunning;
    }

    public static bool IsUriTarget(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return Uri.TryCreate(path.Trim(), UriKind.Absolute, out var uri) && !uri.IsFile;
    }

    public static bool TargetExists(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath)) return false;
        if (IsUriTarget(rawPath)) return true;

        string expanded = Environment.ExpandEnvironmentVariables(rawPath.Trim().Trim('"'));
        return File.Exists(expanded) || Directory.Exists(expanded);
    }

    public static string? ResolveShortcutTarget(string lnkPath)
    {
        try
        {
            if (!lnkPath.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) || !File.Exists(lnkPath))
            {
                return null;
            }

            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null) return null;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell is null) return null;

            try
            {
                dynamic shortcut = shell.CreateShortcut(lnkPath);
                string? target = shortcut.TargetPath as string;
                return string.IsNullOrWhiteSpace(target) ? null : target;
            }
            finally
            {
                if (Marshal.IsComObject(shell))
                {
                    Marshal.ReleaseComObject(shell);
                }
            }
        }
        catch
        {
            return null;
        }
    }

    public async Task<StartupLaunchSummary> LaunchEnabledAppsAsync(
        IEnumerable<StartupAppEntry> apps,
        CancellationToken cancellationToken = default)
    {
        var enabledApps = apps.Where(a => a.Enabled).OrderBy(a => a.Order).ToList();
        var results = new List<AppLaunchItemResult>(enabledApps.Count);

        foreach (var app in enabledApps)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (app.DelaySeconds > 0)
            {
                _logger.Info(LogCategory.Launcher, $"Đợi {app.DelaySeconds}s trước khi mở \"{app.Name}\"...");
                await Task.Delay(TimeSpan.FromSeconds(app.DelaySeconds), cancellationToken).ConfigureAwait(false);
            }

            var itemResult = LaunchSingle(app);
            results.Add(itemResult);
        }

        int launched = results.Count(r => r.Outcome == LaunchOutcome.Launched);
        int skipped = results.Count(r => r.Outcome == LaunchOutcome.SkippedAlreadyRunning);
        int failed = results.Count(r => r.Outcome is LaunchOutcome.MissingFile or LaunchOutcome.Failed);

        return new StartupLaunchSummary(enabledApps.Count, launched, skipped, failed, results);
    }

    public AppLaunchItemResult LaunchSingle(StartupAppEntry app)
    {
        string expandedPath = Environment.ExpandEnvironmentVariables(app.Path.Trim().Trim('"'));
        bool isUri = IsUriTarget(expandedPath);

        if (!isUri && !File.Exists(expandedPath) && !Directory.Exists(expandedPath))
        {
            string msg = $"Không tìm thấy tệp: {expandedPath}";
            _logger.Error(LogCategory.Launcher, $"[{app.Name}] {msg}");
            return new AppLaunchItemResult(app, LaunchOutcome.MissingFile, msg);
        }

        if (app.SkipIfAlreadyRunning && !isUri)
        {
            string? processName = ResolveExpectedProcessName(expandedPath);
            if (!string.IsNullOrWhiteSpace(processName) && _isProcessRunning(processName))
            {
                string msg = $"Đã đang chạy ({processName}.exe)";
                _logger.Info(LogCategory.Launcher, $"[{app.Name}] Bỏ qua vì {msg}");
                return new AppLaunchItemResult(app, LaunchOutcome.SkippedAlreadyRunning, msg);
            }
        }

        try
        {
            string workingDir = string.IsNullOrWhiteSpace(app.WorkingDirectory)
                ? (File.Exists(expandedPath) ? Path.GetDirectoryName(expandedPath) ?? string.Empty : string.Empty)
                : Environment.ExpandEnvironmentVariables(app.WorkingDirectory.Trim().Trim('"'));

            var psi = new ProcessStartInfo
            {
                FileName = expandedPath,
                Arguments = app.Arguments ?? string.Empty,
                WorkingDirectory = Directory.Exists(workingDir) ? workingDir : string.Empty,
                UseShellExecute = true,
                WindowStyle = MapWindowStyle(app.WindowMode)
            };

            if (app.RunAsAdmin)
            {
                psi.Verb = "runas";
            }

            Process.Start(psi);
            _logger.Success(LogCategory.Launcher, $"Đã mở \"{app.Name}\"");
            return new AppLaunchItemResult(app, LaunchOutcome.Launched, "Đã khởi chạy");
        }
        catch (Exception ex)
        {
            _logger.Error(LogCategory.Launcher, $"Không thể mở \"{app.Name}\": {ex.Message}");
            return new AppLaunchItemResult(app, LaunchOutcome.Failed, ex.Message);
        }
    }

    private static string? ResolveExpectedProcessName(string path)
    {
        string target = path;
        if (path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            string? resolved = ResolveShortcutTarget(path);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                target = resolved;
            }
        }

        if (target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetFileNameWithoutExtension(target);
        }

        return null;
    }

    private static bool DefaultIsProcessRunning(string processName)
    {
        try
        {
            return Process.GetProcessesByName(processName).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static ProcessWindowStyle MapWindowStyle(AppWindowMode mode) => mode switch
    {
        AppWindowMode.Minimized => ProcessWindowStyle.Minimized,
        AppWindowMode.Maximized => ProcessWindowStyle.Maximized,
        AppWindowMode.Hidden => ProcessWindowStyle.Hidden,
        _ => ProcessWindowStyle.Normal
    };
}
