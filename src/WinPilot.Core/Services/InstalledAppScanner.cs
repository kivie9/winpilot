using WinPilot.Core.Models;

namespace WinPilot.Core.Services;

/// <summary>
/// Scans Windows Start Menu and Desktop shortcuts quickly without slow COM marshaling.
/// </summary>
public static class InstalledAppScanner
{
    public static IReadOnlyList<InstalledAppItem> ScanShortcuts()
    {
        var roots = new (string Path, string Label)[]
        {
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Start Menu"),
            (Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Start Menu (Cá nhân)"),
            (Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Desktop"),
            (Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "Desktop (Chung)")
        };

        var results = new Dictionary<string, InstalledAppItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var (root, label) in roots)
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (string file in Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (string.IsNullOrWhiteSpace(name) || IsNoiseShortcut(name))
                    {
                        continue;
                    }

                    if (!results.ContainsKey(name))
                    {
                        results[name] = new InstalledAppItem(name, file, label);
                    }
                }
            }
            catch
            {
                // Ignore restricted folders.
            }
        }

        return results.Values
            .OrderBy(item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static bool IsNoiseShortcut(string name)
    {
        string lower = name.ToLowerInvariant();
        return lower.Contains("uninstall") ||
               lower.Contains("gỡ cài đặt") ||
               lower.Contains("readme") ||
               lower.Contains("help") ||
               lower.Contains("documentation");
    }
}
