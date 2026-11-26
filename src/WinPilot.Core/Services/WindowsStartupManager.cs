using Microsoft.Win32;

namespace WinPilot.Core.Services;

/// <summary>
/// Manages WinPilot auto-start registration in HKCU\Software\Microsoft\Windows\CurrentVersion\Run.
/// </summary>
public sealed class WindowsStartupManager
{
    public const string RunRegistryPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string DefaultValueName = "WinPilot";
    public const string StartupArgument = "--startup";

    private readonly string _valueName;

    public WindowsStartupManager(string valueName = DefaultValueName)
    {
        _valueName = valueName;
    }

    public bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            var val = key?.GetValue(_valueName) as string;
            return !string.IsNullOrWhiteSpace(val);
        }
        catch
        {
            return false;
        }
    }

    public string? GetRegisteredCommand()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: false);
            return key?.GetValue(_valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    public void SetEnabled(bool enabled, string? executablePath = null)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunRegistryPath, writable: true)
            ?? Registry.CurrentUser.CreateSubKey(RunRegistryPath, writable: true);

        if (key is null)
        {
            throw new InvalidOperationException("Không thể mở khóa Registry Startup.");
        }

        if (enabled)
        {
            string exe = executablePath ?? Environment.ProcessPath
                ?? throw new InvalidOperationException("Không xác định được đường dẫn file thực thi.");
            string command = BuildCommand(exe);
            key.SetValue(_valueName, command, RegistryValueKind.String);
        }
        else
        {
            if (key.GetValue(_valueName) is not null)
            {
                key.DeleteValue(_valueName, throwOnMissingValue: false);
            }
        }
    }

    public static string BuildCommand(string executablePath)
    {
        string clean = executablePath.Trim().Trim('"');
        return $"\"{clean}\" {StartupArgument}";
    }
}
