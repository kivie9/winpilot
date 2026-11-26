using System.Net.Http.Json;
using System.Text.RegularExpressions;
using WinPilot.Core.Models;

namespace WinPilot.Core.Services;

/// <summary>
/// Sends notifications to Discord, Slack, or custom JSON webhook endpoints.
/// </summary>
public sealed class WebhookNotificationService
{
    private readonly HttpClient _http;
    private readonly ActivityLogger? _logger;

    public WebhookNotificationService(HttpClient? httpClient = null, ActivityLogger? logger = null)
    {
        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        _logger = logger;
    }

    public async Task SendToAllAsync(
        IEnumerable<CustomWebhookChannel> channels,
        bool isStartup,
        string eventLabel,
        string renderedHtmlMessage,
        IReadOnlyList<string> appNames,
        CancellationToken cancellationToken = default)
    {
        var targets = channels
            .Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.Url) &&
                        (isStartup ? c.NotifyOnStartup : c.NotifyOnShutdown))
            .ToList();

        if (targets.Count == 0) return;

        string plainText = StripHtmlTags(renderedHtmlMessage);
        var tasks = targets.Select(c => SendSingleAsync(c, eventLabel, plainText, appNames, cancellationToken));
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<(bool Success, string Detail)> SendSingleAsync(
        CustomWebhookChannel channel,
        string eventLabel,
        string plainMessage,
        IReadOnlyList<string> appNames,
        CancellationToken cancellationToken = default)
    {
        try
        {
            object payload = channel.ChannelType switch
            {
                WebhookChannelType.Discord => new { content = plainMessage },
                WebhookChannelType.Slack => new { text = plainMessage },
                _ => new
                {
                    source = "WinPilot",
                    @event = eventLabel,
                    pcName = Environment.MachineName,
                    user = Environment.UserName,
                    timestamp = DateTimeOffset.Now,
                    apps = appNames,
                    message = plainMessage
                }
            };

            using var resp = await _http.PostAsJsonAsync(channel.Url.Trim(), payload, cancellationToken)
                .ConfigureAwait(false);

            if (resp.IsSuccessStatusCode)
            {
                _logger?.Success(LogCategory.Telegram, $"[Webhook: {channel.Name}] Đã gửi ({(int)resp.StatusCode}).");
                return (true, $"HTTP {(int)resp.StatusCode}");
            }

            string msg = $"HTTP {(int)resp.StatusCode}";
            _logger?.Warning(LogCategory.Telegram, $"[Webhook: {channel.Name}] Lỗi {msg}");
            return (false, msg);
        }
        catch (Exception ex)
        {
            _logger?.Warning(LogCategory.Telegram, $"[Webhook: {channel.Name}] Thất bại: {ex.Message}");
            return (false, ex.Message);
        }
    }

    public static string StripHtmlTags(string html) =>
        Regex.Replace(html ?? string.Empty, "<.*?>", string.Empty);
}
