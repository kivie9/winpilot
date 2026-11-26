using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using WinPilot.Core.Services;

namespace WinPilot.App.Services;

public static class IconExtractor
{
    private const uint ShgfiIcon = 0x000000100;
    private const uint ShgfiLargeIcon = 0x000000000;

    private static readonly ConcurrentDictionary<string, string?> Base64Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? GetBase64PngIcon(string rawPath)
    {
        if (string.IsNullOrWhiteSpace(rawPath) || AppLauncherService.IsUriTarget(rawPath))
        {
            return null;
        }

        return Base64Cache.GetOrAdd(rawPath.Trim(), path =>
        {
            string expanded = Environment.ExpandEnvironmentVariables(path.Trim('"'));
            if (!File.Exists(expanded) && !Directory.Exists(expanded))
            {
                return null;
            }

            // SHGetFileInfo extracts authentic Windows Explorer icons for .lnk, .exe, and folders rapidly.
            string? shellPng = TryExtractViaShell(expanded);
            if (shellPng is not null)
            {
                return shellPng;
            }

            try
            {
                if (!File.Exists(expanded)) return null;
                using Icon? icon = Icon.ExtractAssociatedIcon(expanded);
                if (icon is null) return null;
                return IconToDataUrl(icon);
            }
            catch
            {
                return null;
            }
        });
    }

    private static string? TryExtractViaShell(string path)
    {
        SHFILEINFO shinfo = default;
        IntPtr res = SHGetFileInfo(path, 0, ref shinfo, (uint)Marshal.SizeOf<SHFILEINFO>(), ShgfiIcon | ShgfiLargeIcon);
        if (res == IntPtr.Zero || shinfo.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            using Icon icon = Icon.FromHandle(shinfo.hIcon);
            return IconToDataUrl(icon);
        }
        catch
        {
            return null;
        }
        finally
        {
            DestroyIcon(shinfo.hIcon);
        }
    }

    private static string IconToDataUrl(Icon icon)
    {
        using Bitmap bmp = icon.ToBitmap();
        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string pszPath,
        uint dwFileAttributes,
        ref SHFILEINFO psfi,
        uint cbFileInfo,
        uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);
}
