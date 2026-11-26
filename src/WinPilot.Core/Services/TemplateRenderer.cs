using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using WinPilot.Core.Models;

namespace WinPilot.Core.Services;

public sealed class TemplateContext
{
    public string PcName { get; init; } = Environment.MachineName;
    public string UserName { get; init; } = Environment.UserName;
    public DateTimeOffset Now { get; init; } = DateTimeOffset.Now;
    public TimeSpan Uptime { get; init; } = TimeSpan.FromMilliseconds(Environment.TickCount64);
    public string LocalIp { get; init; } = TemplateRenderer.GetLocalIPv4();
    public string TailscaleIp { get; init; } = TemplateRenderer.GetTailscaleIPv4();
    public string EventLabel { get; init; } = string.Empty;
    public IReadOnlyList<string> AppNames { get; init; } = Array.Empty<string>();
}

public static class TemplateRenderer
{
    public static string Render(string template, TemplateContext context, string parseMode = "HTML")
    {
        if (string.IsNullOrWhiteSpace(template))
        {
            return string.Empty;
        }

        bool escapeHtml = string.Equals(parseMode, "HTML", StringComparison.OrdinalIgnoreCase);
        string Escape(string value) => escapeHtml ? WebUtility.HtmlEncode(value) : value;

        var localNow = context.Now.ToLocalTime();
        string appsList = context.AppNames.Count == 0
            ? "• (Không có ứng dụng nào được bật)"
            : string.Join("\n", context.AppNames.Select(name => $"• {Escape(name)}"));

        var tokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["{pc_name}"] = Escape(context.PcName),
            ["{user}"] = Escape(context.UserName),
            ["{time}"] = localNow.ToString("HH:mm:ss"),
            ["{date}"] = localNow.ToString("dd/MM/yyyy"),
            ["{datetime}"] = localNow.ToString("HH:mm:ss dd/MM/yyyy"),
            ["{uptime}"] = FormatUptime(context.Uptime),
            ["{local_ip}"] = Escape(context.LocalIp),
            ["{tailscale_ip}"] = Escape(context.TailscaleIp),
            ["{apps_count}"] = context.AppNames.Count.ToString(),
            ["{apps_list}"] = appsList,
            ["{event}"] = Escape(context.EventLabel)
        };

        var sb = new StringBuilder(template);
        foreach (var pair in tokens)
        {
            sb.Replace(pair.Key, pair.Value);
        }

        return sb.ToString();
    }

    public static string FormatUptime(TimeSpan uptime)
    {
        if (uptime.TotalSeconds < 60)
        {
            return $"{Math.Max(0, (int)uptime.TotalSeconds)} giây";
        }

        if (uptime.TotalHours < 1)
        {
            return $"{uptime.Minutes} phút {uptime.Seconds} giây";
        }

        if (uptime.TotalDays < 1)
        {
            return $"{(int)uptime.TotalHours} giờ {uptime.Minutes} phút";
        }

        return $"{(int)uptime.TotalDays} ngày {uptime.Hours} giờ {uptime.Minutes} phút";
    }

    public static string GetLocalIPv4()
    {
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                {
                    continue;
                }

                IPInterfaceProperties props = ni.GetIPProperties();
                if (props.GatewayAddresses.Count == 0)
                {
                    continue;
                }

                foreach (UnicastIPAddressInformation addr in props.UnicastAddresses)
                {
                    if (addr.Address.AddressFamily == AddressFamily.InterNetwork &&
                        !IPAddress.IsLoopback(addr.Address))
                    {
                        return addr.Address.ToString();
                    }
                }
            }
        }
        catch
        {
            // Ignore network interface query failures.
        }

        return "Chưa có mạng";
    }

    public static string GetTailscaleIPv4()
    {
        try
        {
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;

                bool isTailscaleAdapter =
                    ni.Name.Contains("Tailscale", StringComparison.OrdinalIgnoreCase) ||
                    ni.Description.Contains("Tailscale", StringComparison.OrdinalIgnoreCase);

                foreach (UnicastIPAddressInformation addr in ni.GetIPProperties().UnicastAddresses)
                {
                    if (addr.Address.AddressFamily != AddressFamily.InterNetwork) continue;

                    byte[] b = addr.Address.GetAddressBytes();
                    bool isTailscaleCgnat = b.Length == 4 && b[0] == 100 && b[1] >= 64 && b[1] <= 127;
                    if (isTailscaleAdapter || isTailscaleCgnat)
                    {
                        return addr.Address.ToString();
                    }
                }
            }

            var psi = new ProcessStartInfo
            {
                FileName = "tailscale",
                Arguments = "ip -4",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var proc = Process.Start(psi);
            if (proc is not null)
            {
                string output = proc.StandardOutput.ReadToEnd().Trim();
                proc.WaitForExit(1500);
                string? firstLine = output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstLine) && IPAddress.TryParse(firstLine.Trim(), out _))
                {
                    return firstLine.Trim();
                }
            }
        }
        catch
        {
            // Tailscale not installed or offline.
        }

        return "Chưa kết nối";
    }
}
