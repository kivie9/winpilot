using System.Net;
using System.Text;
using WinPilot.Core.Models;
using WinPilot.Core.Security;
using WinPilot.Core.Services;

namespace WinPilot.Core.Tests;

public sealed class CoreServicesTests : IDisposable
{
    private readonly string _tempDir;

    public CoreServicesTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "WinPilotTests_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void DpapiProtector_RoundTrips_BotToken_WithoutPlaintextLeak()
    {
        var protector = new DpapiSecretProtector();
        const string token = "123456789:ABCdefGHIjklMNOpqrsTUVwxyz";

        string cipher = protector.Protect(token);
        Assert.StartsWith("dpapi:", cipher);
        Assert.DoesNotContain(token, cipher);

        string decrypted = protector.Unprotect(cipher);
        Assert.Equal(token, decrypted);
    }

    [Fact]
    public void ConfigService_SaveLoad_EncryptsTokenOnDisk_And_StripsOnExport()
    {
        string cfgPath = Path.Combine(_tempDir, "config.json");
        var service = new ConfigService(cfgPath);

        var config = new AppConfig();
        config.Telegram.BotToken = "999888:SecretTokenValue";
        config.Telegram.ChatIds = "111, 222";
        config.StartupApps.Add(new StartupAppEntry { Name = "TestApp", Path = @"C:\Windows\notepad.exe" });

        service.Save(config);

        string rawJson = File.ReadAllText(cfgPath);
        Assert.DoesNotContain("999888:SecretTokenValue", rawJson);

        var loaded = service.Load();
        Assert.Equal("999888:SecretTokenValue", loaded.Telegram.BotToken);
        Assert.Single(loaded.StartupApps);

        string exportPath = Path.Combine(_tempDir, "export.json");
        service.ExportToFile(loaded, exportPath, includeSecrets: false);
        string exportedJson = File.ReadAllText(exportPath);
        Assert.DoesNotContain("999888:SecretTokenValue", exportedJson);
        Assert.Contains("\"EncryptedBotToken\": \"\"", exportedJson);
    }

    [Fact]
    public void TemplateRenderer_ReplacesVariables_And_EscapesHtml()
    {
        var ctx = new TemplateContext
        {
            PcName = "PC<Main>&1",
            UserName = "Long",
            Now = new DateTimeOffset(2026, 11, 26, 9, 30, 0, TimeSpan.FromHours(7)),
            Uptime = TimeSpan.FromMinutes(125),
            LocalIp = "192.168.1.50",
            TailscaleIp = "100.88.12.34",
            EventLabel = "Khởi động",
            AppNames = new[] { "Chrome & Edge", "VSCode" }
        };

        string rendered = TemplateRenderer.Render(
            "<b>{pc_name}</b> {user} {tailscale_ip} {apps_count}\n{apps_list}",
            ctx,
            "HTML");

        Assert.Contains("PC&lt;Main&gt;&amp;1", rendered);
        Assert.Contains("100.88.12.34", rendered);
        Assert.Contains("• Chrome &amp; Edge", rendered);
        Assert.Contains("• VSCode", rendered);
        Assert.Contains("Long 100.88.12.34 2", rendered);
    }

    [Fact]
    public void ParseChatIds_SplitsDeduplicatesAndTrims()
    {
        var ids = TelegramNotificationService.ParseChatIds(" 1001, -100200 ; 1001\n 3003 ");
        Assert.Equal(new[] { "1001", "-100200", "3003" }, ids);
    }

    [Fact]
    public async Task TelegramService_SendsToMultipleRecipients_And_HandlesPartialFailure()
    {
        var handler = new StubHttpHandler(async req =>
        {
            string body = await req.Content!.ReadAsStringAsync();
            if (body.Contains("\"-1002\""))
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent("{\"ok\":false,\"description\":\"Bad Request: chat not found\"}", Encoding.UTF8, "application/json")
                };
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true,\"result\":{\"message_id\":1}}", Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var tg = new TelegramNotificationService(http);

        var result = await tg.SendMessageAsync("tok", "1001, -1002", "Hello");
        Assert.False(result.AllSucceeded);
        Assert.Equal(2, result.Recipients.Count);
        Assert.True(result.Recipients[0].Success);
        Assert.False(result.Recipients[1].Success);
        Assert.Contains("chat not found", result.Recipients[1].Detail);
    }

    [Fact]
    public async Task TelegramService_RetriesTransientNetworkError_ThenSucceeds()
    {
        int calls = 0;
        var handler = new StubHttpHandler(_ =>
        {
            calls++;
            if (calls == 1)
            {
                throw new HttpRequestException("Network is unreachable");
            }

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var tg = new TelegramNotificationService(http);

        var result = await tg.SendWithNetworkRetryAsync("tok", "1001", "Boot", "HTML", maxWaitSeconds: 10);
        Assert.True(result.AllSucceeded);
        Assert.Equal(2, calls);
    }

    [Fact]
    public async Task TelegramService_DiscoverChatIds_ExtractsUniqueChats()
    {
        const string updatesJson = """
        {
          "ok": true,
          "result": [
            { "update_id": 1, "message": { "chat": { "id": 12345, "type": "private", "first_name": "Long", "username": "longdev" } } },
            { "update_id": 2, "message": { "chat": { "id": -100999, "type": "supergroup", "title": "Ops Alerts" } } },
            { "update_id": 3, "message": { "chat": { "id": 12345, "type": "private", "first_name": "Long", "username": "longdev" } } }
          ]
        }
        """;

        var handler = new StubHttpHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(updatesJson, Encoding.UTF8, "application/json")
        });

        using var http = new HttpClient(handler);
        var tg = new TelegramNotificationService(http);

        var (ok, chats, error) = await tg.DiscoverChatIdsAsync("tok");
        Assert.True(ok, error);
        Assert.Equal(2, chats.Count);
        Assert.Contains(chats, c => c.ChatId == "12345" && c.Username == "longdev");
        Assert.Contains(chats, c => c.ChatId == "-100999" && c.Title == "Ops Alerts");
    }

    [Fact]
    public void AppLauncher_SkipsRunningProcess_And_DetectsMissingFile()
    {
        var logger = new ActivityLogger(Path.Combine(_tempDir, "logs"));
        var launcher = new AppLauncherService(logger, procName => string.Equals(procName, "notepad", StringComparison.OrdinalIgnoreCase));

        var notepadPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");
        var skipResult = launcher.LaunchSingle(new StartupAppEntry
        {
            Name = "Notepad",
            Path = notepadPath,
            SkipIfAlreadyRunning = true
        });
        Assert.Equal(LaunchOutcome.SkippedAlreadyRunning, skipResult.Outcome);

        var missingResult = launcher.LaunchSingle(new StartupAppEntry
        {
            Name = "GhostApp",
            Path = Path.Combine(_tempDir, "does-not-exist.exe")
        });
        Assert.Equal(LaunchOutcome.MissingFile, missingResult.Outcome);
    }

    [Fact]
    public void Orchestrator_DeduplicatesShutdownNotification_UntilReset()
    {
        int sendCalls = 0;
        var handler = new StubHttpHandler(_ =>
        {
            Interlocked.Increment(ref sendCalls);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var logger = new ActivityLogger(Path.Combine(_tempDir, "logs"));
        var cfgService = new ConfigService(Path.Combine(_tempDir, "cfg.json"));
        var tg = new TelegramNotificationService(http, logger);
        var launcher = new AppLauncherService(logger);
        var orchestrator = new AutomationOrchestrator(cfgService, launcher, tg, logger);

        var config = new AppConfig();
        config.Telegram.BotToken = "tok";
        config.Telegram.ChatIds = "123";

        var first = orchestrator.ExecuteShutdownNotificationSync(config, ShutdownEventKind.ShutdownOrRestart);
        var second = orchestrator.ExecuteShutdownNotificationSync(config, ShutdownEventKind.ShutdownOrRestart);

        Assert.NotNull(first);
        Assert.True(first!.AllSucceeded);
        Assert.Null(second);
        Assert.Equal(1, sendCalls);

        orchestrator.ResetShutdownGuard();
        var third = orchestrator.ExecuteShutdownNotificationSync(config, ShutdownEventKind.ShutdownOrRestart);
        Assert.NotNull(third);
        Assert.Equal(2, sendCalls);
    }

    [Fact]
    public void ConditionEvaluator_ChecksWeekdayAndWifiSsid()
    {
        var monday = new DateTime(2026, 11, 23, 9, 0, 0); // Monday
        var cond = new StartupConditions
        {
            Enabled = true,
            AllowedDays = new List<int> { 1, 2, 3, 4, 5 },
            AllowedWifiSsids = "Office_5G, HomeWiFi"
        };

        var okEval = new ConditionEvaluator(() => "Office_5G", () => monday);
        Assert.True(okEval.Evaluate(cond).Allowed);

        var wrongWifi = new ConditionEvaluator(() => "Starbucks_Free", () => monday);
        Assert.False(wrongWifi.Evaluate(cond).Allowed);

        var sunday = new DateTime(2026, 11, 22, 9, 0, 0); // Sunday
        var wrongDay = new ConditionEvaluator(() => "Office_5G", () => sunday);
        Assert.False(wrongDay.Evaluate(cond).Allowed);
    }

    [Fact]
    public async Task WebhookService_FormatsDiscordAndSlackPayloads()
    {
        var capturedBodies = new List<string>();
        var handler = new StubHttpHandler(async req =>
        {
            capturedBodies.Add(await req.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.NoContent);
        });

        using var http = new HttpClient(handler);
        var svc = new WebhookNotificationService(http);

        var channels = new[]
        {
            new CustomWebhookChannel { Name = "DC", ChannelType = WebhookChannelType.Discord, Url = "https://example.com/dc", Enabled = true },
            new CustomWebhookChannel { Name = "SL", ChannelType = WebhookChannelType.Slack, Url = "https://example.com/sl", Enabled = true }
        };

        await svc.SendToAllAsync(channels, isStartup: true, "Khởi động", "<b>Boot</b> OK", new[] { "Chrome" });

        Assert.Equal(2, capturedBodies.Count);
        Assert.Contains(capturedBodies, b => b.Contains("\"content\":\"Boot OK\""));
        Assert.Contains(capturedBodies, b => b.Contains("\"text\":\"Boot OK\""));
    }

    [Fact]
    public async Task TelegramCommandListener_IgnoresUnauthorizedChat_And_RepliesToOwner()
    {
        var sentPayloads = new List<string>();
        var handler = new StubHttpHandler(async req =>
        {
            sentPayloads.Add(await req.Content!.ReadAsStringAsync());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var logger = new ActivityLogger(Path.Combine(_tempDir, "logs"));
        var cfgService = new ConfigService(Path.Combine(_tempDir, "cfg.json"));
        var tg = new TelegramNotificationService(http, logger);
        var launcher = new AppLauncherService(logger);
        var orchestrator = new AutomationOrchestrator(cfgService, launcher, tg, logger);

        var config = new AppConfig();
        config.Telegram.BotToken = "tok";
        config.Telegram.ChatIds = "777";
        config.Telegram.EnableRemoteCommands = true;

        using var listener = new TelegramCommandListener(() => config, orchestrator, logger, http);

        const string updates = """
        {
          "ok": true,
          "result": [
            { "update_id": 10, "message": { "chat": { "id": 999 }, "text": "/status" } },
            { "update_id": 11, "message": { "chat": { "id": 777 }, "text": "/status" } }
          ]
        }
        """;

        await listener.ProcessUpdateJsonAsync(updates, config);

        Assert.Single(sentPayloads);
        Assert.Contains("777", sentPayloads[0]);
        Assert.DoesNotContain("999", sentPayloads[0]);
    }

    [Fact]
    public async Task TelegramService_ReEvaluatesMessageFactory_OnNetworkRetry()
    {
        int calls = 0;
        string? deliveredBody = null;
        var handler = new StubHttpHandler(async req =>
        {
            calls++;
            if (calls == 1)
            {
                throw new HttpRequestException("DNS not ready");
            }
            deliveredBody = await req.Content!.ReadAsStringAsync();
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json")
            };
        });

        using var http = new HttpClient(handler);
        var tg = new TelegramNotificationService(http);

        var result = await tg.SendWithNetworkRetryAsync(
            "tok",
            "1001",
            () => calls == 0 ? "IP: Offline" : "IP: 192.168.1.99",
            "HTML",
            maxWaitSeconds: 10);

        Assert.True(result.AllSucceeded);
        Assert.Equal(2, calls);
        Assert.Contains("192.168.1.99", deliveredBody);
    }

    private sealed class StubHttpHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, Task<HttpResponseMessage>> _responder;

        public StubHttpHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = req => Task.FromResult(responder(req));
        }

        public StubHttpHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _responder(request);
        }
    }
}
