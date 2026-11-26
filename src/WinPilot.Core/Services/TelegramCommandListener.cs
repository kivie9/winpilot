using System.Net.Http.Json;
using System.Text.Json;
using WinPilot.Core.Models;

namespace WinPilot.Core.Services;

/// <summary>
/// Optional background long-polling listener for Telegram bot commands from authorized Chat IDs.
/// </summary>
public sealed class TelegramCommandListener : IDisposable
{
    private readonly HttpClient _http;
    private readonly Func<AppConfig> _configProvider;
    private readonly AutomationOrchestrator _orchestrator;
    private readonly ActivityLogger _logger;

    private CancellationTokenSource? _cts;
    private long _lastUpdateOffset;

    public TelegramCommandListener(
        Func<AppConfig> configProvider,
        AutomationOrchestrator orchestrator,
        ActivityLogger logger,
        HttpClient? httpClient = null)
    {
        _configProvider = configProvider;
        _orchestrator = orchestrator;
        _logger = logger;
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(35) };
    }

    public void SyncState()
    {
        var cfg = _configProvider();
        bool shouldRun = cfg.Telegram.Enabled &&
                         cfg.Telegram.EnableRemoteCommands &&
                         !string.IsNullOrWhiteSpace(cfg.Telegram.BotToken);

        if (shouldRun && _cts is null)
        {
            _cts = new CancellationTokenSource();
            _ = PollLoopAsync(_cts.Token);
            _logger.Info(LogCategory.Telegram, "Đã bật lắng nghe lệnh điều khiển từ xa qua Telegram.");
        }
        else if (!shouldRun && _cts is not null)
        {
            _cts.Cancel();
            _cts.Dispose();
            _cts = null;
            _logger.Info(LogCategory.Telegram, "Đã tắt lắng nghe lệnh Telegram từ xa.");
        }
    }

    public async Task ProcessUpdateJsonAsync(string jsonBody, AppConfig config, CancellationToken ct = default)
    {
        using var doc = JsonDocument.Parse(jsonBody);
        if (!doc.RootElement.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean()) return;
        if (!doc.RootElement.TryGetProperty("result", out var updates) || updates.ValueKind != JsonValueKind.Array) return;

        var allowedIds = GetAuthorizedChatIds(config.Telegram);
        if (allowedIds.Count == 0) return;

        foreach (var update in updates.EnumerateArray())
        {
            if (update.TryGetProperty("update_id", out var uid))
            {
                _lastUpdateOffset = Math.Max(_lastUpdateOffset, uid.GetInt64() + 1);
            }

            if (!update.TryGetProperty("message", out var msg)) continue;
            if (!msg.TryGetProperty("chat", out var chat) || !chat.TryGetProperty("id", out var idEl)) continue;
            if (!msg.TryGetProperty("text", out var textEl)) continue;

            string chatId = idEl.ToString();
            if (!allowedIds.Contains(chatId, StringComparer.OrdinalIgnoreCase)) continue;

            string text = (textEl.GetString() ?? string.Empty).Trim();
            if (!text.StartsWith('/')) continue;

            string cmd = text.Split(' ', '@')[0].ToLowerInvariant();
            string reply = await ExecuteCommandAsync(cmd, config, ct).ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(reply))
            {
                await SendReplyAsync(config.Telegram.BotToken.Trim(), chatId, reply, ct).ConfigureAwait(false);
            }
        }
    }

    public static IReadOnlyList<string> GetAuthorizedChatIds(TelegramSettings tg)
    {
        string raw = string.IsNullOrWhiteSpace(tg.AllowedCommandChatIds) ? tg.ChatIds : tg.AllowedCommandChatIds;
        return TelegramNotificationService.ParseChatIds(raw);
    }

    private async Task<string> ExecuteCommandAsync(string cmd, AppConfig config, CancellationToken ct)
    {
        switch (cmd)
        {
            case "/status":
            {
                string uptime = TemplateRenderer.FormatUptime(TimeSpan.FromMilliseconds(Environment.TickCount64));
                string profileName = config.Profiles.FirstOrDefault(p => p.Id == config.ActiveProfileId)?.Name ?? "Mặc định";
                int enabledApps = config.StartupApps.Count(a => a.ProfileId == config.ActiveProfileId && a.Enabled);
                return $"🟢 <b>WinPilot đang hoạt động</b>\n" +
                       $"• Máy: <code>{Environment.MachineName}</code> ({Environment.UserName})\n" +
                       $"• IP LAN: <code>{TemplateRenderer.GetLocalIPv4()}</code>\n" +
                       $"• Uptime: <code>{uptime}</code>\n" +
                       $"• Profile: <b>{profileName}</b> ({enabledApps} app bật)";
            }

            case "/apps":
            {
                var apps = config.StartupApps.Where(a => a.ProfileId == config.ActiveProfileId).OrderBy(a => a.Order).ToList();
                if (apps.Count == 0) return "Danh sách ứng dụng hiện đang trống.";
                var lines = apps.Select(a => $"• {(a.Enabled ? "✅" : "⏸")} {System.Net.WebUtility.HtmlEncode(a.Name)}");
                return "<b>Danh sách ứng dụng:</b>\n" + string.Join("\n", lines);
            }

            case "/run":
                _ = _orchestrator.RunStartupWorkflowAsync(config, isBootLaunch: false, ct);
                return "🚀 Đang kích hoạt mở các ứng dụng trong Profile hiện tại...";

            case "/help":
            case "/start":
                return "<b>Lệnh điều khiển WinPilot:</b>\n" +
                       "/status — Xem trạng thái máy &amp; uptime\n" +
                       "/apps — Xem danh sách ứng dụng\n" +
                       "/run — Mở ngay các ứng dụng khởi động";

            default:
                return string.Empty;
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cfg = _configProvider();
                string token = cfg.Telegram.BotToken.Trim();
                if (string.IsNullOrWhiteSpace(token))
                {
                    await Task.Delay(3000, ct).ConfigureAwait(false);
                    continue;
                }

                string url = $"https://api.telegram.org/bot{token}/getUpdates?offset={_lastUpdateOffset}&timeout=25";
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    await ProcessUpdateJsonAsync(body, cfg, ct).ConfigureAwait(false);
                }
                else
                {
                    await Task.Delay(5000, ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try { await Task.Delay(5000, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private async Task SendReplyAsync(string botToken, string chatId, string text, CancellationToken ct)
    {
        try
        {
            string url = $"https://api.telegram.org/bot{botToken}/sendMessage";
            await _http.PostAsJsonAsync(url, new
            {
                chat_id = chatId,
                text,
                parse_mode = "HTML",
                disable_web_page_preview = true
            }, ct).ConfigureAwait(false);
        }
        catch
        {
            // Ignore transient reply errors.
        }
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }
}
