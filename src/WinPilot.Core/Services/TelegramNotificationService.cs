using System.Net;
using System.Net.Http.Json;
using System.Net.NetworkInformation;
using System.Text.Json;
using WinPilot.Core.Models;

namespace WinPilot.Core.Services;

/// <summary>
/// Sends notifications and discovers chats via the Telegram Bot HTTP API.
/// </summary>
public sealed class TelegramNotificationService
{
    private readonly HttpClient _http;
    private readonly ActivityLogger? _logger;

    public TelegramNotificationService(HttpClient? httpClient = null, ActivityLogger? logger = null)
    {
        _http = httpClient ?? new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _logger = logger;
    }

    public static IReadOnlyList<string> ParseChatIds(string? rawChatIds)
    {
        if (string.IsNullOrWhiteSpace(rawChatIds))
        {
            return Array.Empty<string>();
        }

        return rawChatIds
            .Split(new[] { ',', ';', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(id => id.Trim())
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<TelegramBotInfo> TestBotAsync(string botToken, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(botToken))
        {
            return new TelegramBotInfo(false, string.Empty, string.Empty, "Chưa nhập Bot Token.");
        }

        try
        {
            string url = $"https://api.telegram.org/bot{botToken.Trim()}/getMe";
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);
            bool ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.GetBoolean();
            if (!ok)
            {
                string desc = doc.RootElement.TryGetProperty("description", out var descEl)
                    ? descEl.GetString() ?? "Lỗi không xác định"
                    : $"HTTP {(int)response.StatusCode}";
                return new TelegramBotInfo(false, string.Empty, string.Empty, desc);
            }

            var result = doc.RootElement.GetProperty("result");
            string username = result.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
            string firstName = result.TryGetProperty("first_name", out var f) ? f.GetString() ?? "" : "";
            return new TelegramBotInfo(true, username, firstName, string.Empty);
        }
        catch (Exception ex)
        {
            return new TelegramBotInfo(false, string.Empty, string.Empty, ex.Message);
        }
    }

    public async Task<(bool Success, IReadOnlyList<DiscoveredChat> Chats, string Error)> DiscoverChatIdsAsync(
        string botToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(botToken))
        {
            return (false, Array.Empty<DiscoveredChat>(), "Vui lòng nhập Bot Token trước.");
        }

        try
        {
            string url = $"https://api.telegram.org/bot{botToken.Trim()}/getUpdates?limit=30";
            using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            using var doc = JsonDocument.Parse(body);
            if (!doc.RootElement.TryGetProperty("ok", out var okEl) || !okEl.GetBoolean())
            {
                string desc = doc.RootElement.TryGetProperty("description", out var descEl)
                    ? descEl.GetString() ?? "Không đọc được getUpdates"
                    : $"HTTP {(int)response.StatusCode}";
                return (false, Array.Empty<DiscoveredChat>(), desc);
            }

            var found = new Dictionary<string, DiscoveredChat>(StringComparer.Ordinal);
            if (doc.RootElement.TryGetProperty("result", out var results) && results.ValueKind == JsonValueKind.Array)
            {
                foreach (var update in results.EnumerateArray())
                {
                    TryExtractChat(update, "message", found);
                    TryExtractChat(update, "edited_message", found);
                    TryExtractChat(update, "channel_post", found);
                    TryExtractChat(update, "my_chat_member", found);
                }
            }

            return (true, found.Values.ToList(), string.Empty);
        }
        catch (Exception ex)
        {
            return (false, Array.Empty<DiscoveredChat>(), ex.Message);
        }
    }

    public async Task<NotificationBatchResult> SendMessageAsync(
        string botToken,
        string rawChatIds,
        string message,
        string parseMode = "HTML",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(botToken))
        {
            return new NotificationBatchResult(false, false, Array.Empty<RecipientSendResult>(), "Thiếu Bot Token");
        }

        var chatIds = ParseChatIds(rawChatIds);
        if (chatIds.Count == 0)
        {
            return new NotificationBatchResult(false, false, Array.Empty<RecipientSendResult>(), "Thiếu Chat ID");
        }

        string token = botToken.Trim();
        var tasks = chatIds.Select(chatId => SendSingleAsync(token, chatId, message, parseMode, cancellationToken));
        var results = await Task.WhenAll(tasks).ConfigureAwait(false);

        int okCount = results.Count(r => r.Success);
        bool allOk = okCount == results.Length;
        string summary = allOk
            ? $"Đã gửi thành công đến {okCount} người/nhóm nhận"
            : $"Gửi thành công {okCount}/{results.Length}: {string.Join("; ", results.Where(r => !r.Success).Select(r => $"{r.ChatId} ({r.Detail})"))}";

        return new NotificationBatchResult(allOk, true, results, summary);
    }

    /// <summary>
    /// Retries transient network errors during Windows boot/wake until internet is ready or timeout expires.
    /// Uses a message factory so dynamic fields like {local_ip} are resolved after network connects.
    /// </summary>
    public Task<NotificationBatchResult> SendWithNetworkRetryAsync(
        string botToken,
        string rawChatIds,
        string message,
        string parseMode,
        int maxWaitSeconds,
        CancellationToken cancellationToken = default) =>
        SendWithNetworkRetryAsync(botToken, rawChatIds, () => message, parseMode, maxWaitSeconds, cancellationToken);

    public async Task<NotificationBatchResult> SendWithNetworkRetryAsync(
        string botToken,
        string rawChatIds,
        Func<string> messageFactory,
        string parseMode,
        int maxWaitSeconds,
        CancellationToken cancellationToken = default)
    {
        int clampedWait = Math.Clamp(maxWaitSeconds, 0, 300);
        var deadline = DateTime.UtcNow.AddSeconds(clampedWait);
        int[] delays = { 2, 3, 4, 5, 5, 8 };
        int attempt = 0;

        // Wait briefly for any network adapter to report Up before first HTTP call.
        while (!NetworkInterface.GetIsNetworkAvailable() && DateTime.UtcNow < deadline)
        {
            if (attempt == 0)
            {
                _logger?.Info(LogCategory.Telegram, "Mạng chưa sẵn sàng lúc khởi động, đang chờ kết nối...");
            }
            attempt++;
            await WaitForNetworkOrDelayAsync(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        attempt = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string message = messageFactory();
            var result = await SendMessageAsync(botToken, rawChatIds, message, parseMode, cancellationToken)
                .ConfigureAwait(false);

            if (result.AllSucceeded || !result.HasAttempted)
            {
                return result;
            }

            // Only retry when failure looks like transient network/connectivity (no HTTP 400/401/403/404 from Telegram).
            bool hasPermanentTelegramError = result.Recipients.All(r =>
                r.StatusCode is >= 400 and < 500 && r.StatusCode != 429);

            if (hasPermanentTelegramError || DateTime.UtcNow >= deadline)
            {
                return result;
            }

            int delaySec = delays[Math.Min(attempt, delays.Length - 1)];
            attempt++;
            _logger?.Info(LogCategory.Telegram, $"Chưa kết nối được Telegram, thử gửi lại sau {delaySec}s...");

            TimeSpan remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                return result;
            }

            TimeSpan sleep = remaining < TimeSpan.FromSeconds(delaySec)
                ? remaining
                : TimeSpan.FromSeconds(delaySec);

            await WaitForNetworkOrDelayAsync(sleep, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task WaitForNetworkOrDelayAsync(TimeSpan delay, CancellationToken cancellationToken)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(object? s, NetworkAvailabilityEventArgs e)
        {
            if (e.IsAvailable) tcs.TrySetResult(true);
        }

        NetworkChange.NetworkAvailabilityChanged += Handler;
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delayTask = Task.Delay(delay, linked.Token);
            await Task.WhenAny(delayTask, tcs.Task).ConfigureAwait(false);
            linked.Cancel();
        }
        finally
        {
            NetworkChange.NetworkAvailabilityChanged -= Handler;
        }
    }

    private async Task<RecipientSendResult> SendSingleAsync(
        string botToken,
        string chatId,
        string message,
        string parseMode,
        CancellationToken cancellationToken)
    {
        try
        {
            string url = $"https://api.telegram.org/bot{botToken}/sendMessage";
            var payload = new Dictionary<string, object?>
            {
                ["chat_id"] = chatId,
                ["text"] = message,
                ["disable_web_page_preview"] = true
            };

            if (!string.IsNullOrWhiteSpace(parseMode))
            {
                payload["parse_mode"] = parseMode;
            }

            using var response = await _http.PostAsJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            int code = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                return new RecipientSendResult(chatId, true, code, "OK");
            }

            string detail = ExtractTelegramError(body, response.StatusCode);
            return new RecipientSendResult(chatId, false, code, detail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new RecipientSendResult(chatId, false, null, "Hết thời gian chờ kết nối Telegram");
        }
        catch (HttpRequestException ex)
        {
            return new RecipientSendResult(chatId, false, null, $"Lỗi mạng: {ex.Message}");
        }
        catch (Exception ex)
        {
            return new RecipientSendResult(chatId, false, null, ex.Message);
        }
    }

    private static string ExtractTelegramError(string body, HttpStatusCode statusCode)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("description", out var desc))
            {
                return desc.GetString() ?? $"HTTP {(int)statusCode}";
            }
        }
        catch
        {
            // Non-JSON response body.
        }

        return $"HTTP {(int)statusCode}";
    }

    private static void TryExtractChat(JsonElement update, string containerProp, Dictionary<string, DiscoveredChat> found)
    {
        if (!update.TryGetProperty(containerProp, out var container)) return;
        if (!container.TryGetProperty("chat", out var chat)) return;
        if (!chat.TryGetProperty("id", out var idEl)) return;

        string id = idEl.ToString();
        string type = chat.TryGetProperty("type", out var t) ? t.GetString() ?? "unknown" : "unknown";
        string username = chat.TryGetProperty("username", out var u) ? u.GetString() ?? "" : "";
        string title = chat.TryGetProperty("title", out var ttl) ? ttl.GetString() ?? "" : "";

        if (string.IsNullOrWhiteSpace(title))
        {
            string first = chat.TryGetProperty("first_name", out var fn) ? fn.GetString() ?? "" : "";
            string last = chat.TryGetProperty("last_name", out var ln) ? ln.GetString() ?? "" : "";
            title = $"{first} {last}".Trim();
            if (string.IsNullOrWhiteSpace(title)) title = username;
            if (string.IsNullOrWhiteSpace(title)) title = "Telegram Chat";
        }

        found[id] = new DiscoveredChat(id, title, type, username);
    }
}
