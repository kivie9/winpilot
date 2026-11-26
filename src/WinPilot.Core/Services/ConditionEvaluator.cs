using System.Diagnostics;
using WinPilot.Core.Models;

namespace WinPilot.Core.Services;

public sealed record ConditionCheckResult(bool Allowed, string Reason);

/// <summary>
/// Evaluates weekday and Wi-Fi SSID conditions before auto-launching startup apps.
/// </summary>
public sealed class ConditionEvaluator
{
    private readonly Func<string?> _wifiSsidProvider;
    private readonly Func<DateTime> _nowProvider;

    public ConditionEvaluator(
        Func<string?>? wifiSsidProvider = null,
        Func<DateTime>? nowProvider = null)
    {
        _wifiSsidProvider = wifiSsidProvider ?? GetCurrentWifiSsid;
        _nowProvider = nowProvider ?? (() => DateTime.Now);
    }

    public ConditionCheckResult Evaluate(StartupConditions conditions)
    {
        if (!conditions.Enabled)
        {
            return new ConditionCheckResult(true, "Điều kiện đang tắt.");
        }

        var now = _nowProvider();
        if (conditions.AllowedDays.Count > 0 && !conditions.AllowedDays.Contains((int)now.DayOfWeek))
        {
            return new ConditionCheckResult(false, $"Hôm nay ({now:dddd}) không nằm trong lịch chạy.");
        }

        if (!string.IsNullOrWhiteSpace(conditions.AllowedWifiSsids))
        {
            var allowed = conditions.AllowedWifiSsids
                .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();

            if (allowed.Count > 0)
            {
                string? currentSsid = _wifiSsidProvider();
                if (string.IsNullOrWhiteSpace(currentSsid) ||
                    !allowed.Any(a => string.Equals(a, currentSsid, StringComparison.OrdinalIgnoreCase)))
                {
                    return new ConditionCheckResult(
                        false,
                        $"Wi-Fi hiện tại ('{currentSsid ?? "Không có"}') không khớp danh sách cho phép.");
                }
            }
        }

        return new ConditionCheckResult(true, "Đạt tất cả điều kiện.");
    }

    public static string? GetCurrentWifiSsid()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "netsh",
                Arguments = "wlan show interfaces",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is null) return null;
            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(1500);

            foreach (string rawLine in output.Split('\n'))
            {
                string line = rawLine.Trim();
                if (line.StartsWith("SSID", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("BSSID", StringComparison.OrdinalIgnoreCase))
                {
                    int colon = line.IndexOf(':');
                    if (colon > 0)
                    {
                        string ssid = line[(colon + 1)..].Trim();
                        if (!string.IsNullOrWhiteSpace(ssid)) return ssid;
                    }
                }
            }
        }
        catch
        {
            // Ignore when WLAN service or adapter is unavailable.
        }

        return null;
    }
}
