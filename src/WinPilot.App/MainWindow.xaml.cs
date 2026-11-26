using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using WinPilot.App.Helpers;
using WinPilot.App.Services;
using WinPilot.Core.Models;
using WinPilot.Core.Services;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;

namespace WinPilot.App;

public partial class MainWindow : Window
{
    private static readonly JsonSerializerOptions BridgeJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly AppConfig _config;
    private readonly ConfigService _configService;
    private readonly ActivityLogger _logger;
    private readonly WindowsStartupManager _startupManager;
    private readonly AppLauncherService _launcher;
    private readonly TelegramNotificationService _telegram;
    private readonly WebhookNotificationService _webhooks;
    private readonly AutomationOrchestrator _orchestrator;
    private readonly TelegramCommandListener _commandListener;
    private readonly Action _onTrayRefresh;
    private readonly Func<bool> _isExitingProvider;

    private bool _webReady;

    public MainWindow(
        AppConfig config,
        ConfigService configService,
        ActivityLogger logger,
        WindowsStartupManager startupManager,
        AppLauncherService launcher,
        TelegramNotificationService telegram,
        WebhookNotificationService webhooks,
        AutomationOrchestrator orchestrator,
        TelegramCommandListener commandListener,
        Action onTrayRefresh,
        Func<bool> isExitingProvider)
    {
        InitializeComponent();
        WindowThemeHelper.ApplyDarkTitleBar(this);

        _config = config;
        _configService = configService;
        _logger = logger;
        _startupManager = startupManager;
        _launcher = launcher;
        _telegram = telegram;
        _webhooks = webhooks;
        _orchestrator = orchestrator;
        _commandListener = commandListener;
        _onTrayRefresh = onTrayRefresh;
        _isExitingProvider = isExitingProvider;

        _logger.EntryAdded += _ => PushEvent("logsUpdated", GetLogsSnapshot());
        _logger.Cleared += () => PushEvent("logsUpdated", GetLogsSnapshot());
        _orchestrator.StateChanged += () => PushEvent("stateUpdated", BuildStatePayload());

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (_webReady) return;

        string userDataDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinPilot",
            "WebView2");

        var env = await CoreWebView2Environment.CreateAsync(null, userDataDir);
        await WebView.EnsureCoreWebView2Async(env);

        var core = WebView.CoreWebView2;
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = true;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = false;

        core.AddWebResourceRequestedFilter("https://winpilot.app/*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += OnWebResourceRequested;
        core.WebMessageReceived += OnWebMessageReceived;

        _webReady = true;
        core.Navigate("https://winpilot.app/index.html");
    }

    private void Host_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Host_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;

        int added = 0;
        foreach (string file in files)
        {
            string name = Path.GetFileNameWithoutExtension(file);
            _config.StartupApps.Add(new StartupAppEntry
            {
                ProfileId = _config.ActiveProfileId,
                Name = string.IsNullOrWhiteSpace(name) ? file : name,
                Path = file,
                Order = _config.StartupApps.Count
            });
            added++;
            _logger.Info(LogCategory.Settings, $"Đã kéo thả thêm ứng dụng: {name}");
        }

        if (added > 0)
        {
            _configService.Save(_config);
            PushEvent("stateUpdated", BuildStatePayload());
        }
    }

    private static void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (sender is not CoreWebView2 core) return;

        var uri = new Uri(e.Request.Uri);
        string relative = uri.AbsolutePath.TrimStart('/');
        if (string.IsNullOrEmpty(relative)) relative = "index.html";

        string resourceName = $"WinPilot.App.wwwroot.{relative.Replace('/', '.')}";
        Stream? stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName);
        if (stream is null)
        {
            e.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
            return;
        }

        string mime = Path.GetExtension(relative).ToLowerInvariant() switch
        {
            ".html" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".svg" => "image/svg+xml",
            ".png" => "image/png",
            _ => "application/octet-stream"
        };

        e.Response = core.Environment.CreateWebResourceResponse(stream, 200, "OK", $"Content-Type: {mime}");
    }

    private async void OnWebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string id = string.Empty;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() ?? "" : "";
            string action = root.GetProperty("action").GetString() ?? "";
            JsonElement payload = root.TryGetProperty("payload", out var pEl) ? pEl : default;

            object? result = await HandleActionAsync(action, payload);
            Reply(id, true, result, null);
        }
        catch (Exception ex)
        {
            Reply(id, false, null, ex.Message);
        }
    }

    private async Task<object?> HandleActionAsync(string action, JsonElement payload)
    {
        switch (action)
        {
            case "getState":
                return BuildStatePayload();

            case "toggleAutoStart":
            {
                bool enabled = payload.GetProperty("enabled").GetBoolean();
                _startupManager.SetEnabled(enabled);
                _config.General.AutoStartWithWindows = enabled;
                _configService.Save(_config);
                _logger.Success(LogCategory.Settings, enabled
                    ? "Đã bật tự khởi động WinPilot cùng Windows."
                    : "Đã tắt tự khởi động WinPilot cùng Windows.");
                return BuildStatePayload();
            }

            case "setActiveProfile":
            {
                string profileId = payload.GetProperty("profileId").GetString() ?? "default";
                if (_config.Profiles.Any(p => p.Id == profileId))
                {
                    _config.ActiveProfileId = profileId;
                    _configService.Save(_config);
                    _onTrayRefresh();
                }
                return BuildStatePayload();
            }

            case "createProfile":
            {
                string name = (payload.GetProperty("name").GetString() ?? "").Trim();
                if (string.IsNullOrWhiteSpace(name)) throw new InvalidOperationException("Tên Profile không được trống.");
                var profile = new AppProfile { Id = Guid.NewGuid().ToString("N")[..8], Name = name };
                _config.Profiles.Add(profile);
                _config.ActiveProfileId = profile.Id;
                _configService.Save(_config);
                _onTrayRefresh();
                _logger.Info(LogCategory.Settings, $"Đã tạo Profile mới: {name}");
                return BuildStatePayload();
            }

            case "deleteProfile":
            {
                string profileId = payload.GetProperty("profileId").GetString() ?? "";
                if (_config.Profiles.Count <= 1) throw new InvalidOperationException("Phải giữ lại ít nhất 1 Profile.");
                _config.Profiles.RemoveAll(p => p.Id == profileId);
                _config.StartupApps.RemoveAll(a => a.ProfileId == profileId);
                if (_config.ActiveProfileId == profileId) _config.ActiveProfileId = _config.Profiles[0].Id;
                _configService.Save(_config);
                _onTrayRefresh();
                return BuildStatePayload();
            }

            case "browseAppFiles":
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Chọn ứng dụng cần tự mở",
                    Multiselect = true,
                    Filter = "Ứng dụng & Shortcut (*.exe;*.lnk;*.bat;*.cmd)|*.exe;*.lnk;*.bat;*.cmd|Tất cả tệp (*.*)|*.*"
                };

                if (dlg.ShowDialog(this) == true)
                {
                    foreach (string file in dlg.FileNames)
                    {
                        string name = Path.GetFileNameWithoutExtension(file);
                        _config.StartupApps.Add(new StartupAppEntry
                        {
                            ProfileId = _config.ActiveProfileId,
                            Name = string.IsNullOrWhiteSpace(name) ? file : name,
                            Path = file,
                            Order = _config.StartupApps.Count
                        });
                        _logger.Info(LogCategory.Settings, $"Đã thêm ứng dụng: {name}");
                    }
                    _configService.Save(_config);
                }

                return BuildStatePayload();
            }

            case "browseWorkDir":
            {
                using var dlg = new System.Windows.Forms.FolderBrowserDialog
                {
                    Description = "Chọn thư mục làm việc cho ứng dụng"
                };
                string? selected = dlg.ShowDialog() == System.Windows.Forms.DialogResult.OK ? dlg.SelectedPath : null;
                return new { path = selected };
            }

            case "scanInstalledApps":
            {
                var items = await Task.Run(() => InstalledAppScanner.ScanShortcuts()
                    .Select(x => new { x.Name, x.FilePath, x.SourceLabel })
                    .ToList());
                return items;
            }

            case "getAppIcons":
            {
                var paths = JsonSerializer.Deserialize<List<string>>(payload.GetProperty("paths").GetRawText(), BridgeJsonOptions) ?? new();
                var map = await Task.Run(() =>
                {
                    var dict = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                    foreach (string p in paths.Distinct(StringComparer.OrdinalIgnoreCase))
                    {
                        dict[p] = IconExtractor.GetBase64PngIcon(p);
                    }
                    return dict;
                });
                return map;
            }

            case "addInstalledApps":
            {
                var items = JsonSerializer.Deserialize<List<InstalledAppAddDto>>(payload.GetRawText(), BridgeJsonOptions) ?? new();
                foreach (var item in items)
                {
                    _config.StartupApps.Add(new StartupAppEntry
                    {
                        ProfileId = _config.ActiveProfileId,
                        Name = item.Name,
                        Path = item.FilePath,
                        Order = _config.StartupApps.Count
                    });
                    _logger.Info(LogCategory.Settings, $"Đã thêm ứng dụng: {item.Name}");
                }
                _configService.Save(_config);
                return BuildStatePayload();
            }

            case "saveApp":
            {
                var dto = JsonSerializer.Deserialize<StartupAppEntry>(payload.GetRawText(), BridgeJsonOptions)
                    ?? throw new InvalidDataException("Dữ liệu ứng dụng không hợp lệ.");

                if (string.IsNullOrWhiteSpace(dto.ProfileId)) dto.ProfileId = _config.ActiveProfileId;
                if (string.IsNullOrWhiteSpace(dto.Name))
                {
                    dto.Name = Path.GetFileNameWithoutExtension(dto.Path);
                    if (string.IsNullOrWhiteSpace(dto.Name)) dto.Name = dto.Path;
                }

                int existingIdx = _config.StartupApps.FindIndex(a => a.Id == dto.Id);
                if (existingIdx >= 0)
                {
                    dto.Order = existingIdx;
                    _config.StartupApps[existingIdx] = dto;
                    _logger.Info(LogCategory.Settings, $"Đã cập nhật ứng dụng: {dto.Name}");
                }
                else
                {
                    dto.Id = string.IsNullOrWhiteSpace(dto.Id) ? Guid.NewGuid().ToString("N") : dto.Id;
                    dto.Order = _config.StartupApps.Count;
                    _config.StartupApps.Add(dto);
                    _logger.Info(LogCategory.Settings, $"Đã thêm ứng dụng: {dto.Name}");
                }

                _configService.Save(_config);
                return BuildStatePayload();
            }

            case "toggleAppEnabled":
            {
                string appId = payload.GetProperty("id").GetString() ?? "";
                bool enabled = payload.GetProperty("enabled").GetBoolean();
                var app = _config.StartupApps.FirstOrDefault(a => a.Id == appId);
                if (app is not null)
                {
                    app.Enabled = enabled;
                    _configService.Save(_config);
                }
                return BuildStatePayload();
            }

            case "reorderApps":
            {
                var ids = JsonSerializer.Deserialize<List<string>>(payload.GetProperty("orderedIds").GetRawText(), BridgeJsonOptions) ?? new();
                var map = _config.StartupApps.ToDictionary(a => a.Id);
                int order = 0;
                foreach (string appId in ids)
                {
                    if (map.TryGetValue(appId, out var item))
                    {
                        item.Order = order++;
                    }
                }
                _config.StartupApps = _config.StartupApps.OrderBy(a => a.Order).ToList();
                _configService.Save(_config);
                return BuildStatePayload();
            }

            case "deleteApp":
            {
                string appId = payload.GetProperty("id").GetString() ?? "";
                var app = _config.StartupApps.FirstOrDefault(a => a.Id == appId);
                if (app is not null)
                {
                    _config.StartupApps.Remove(app);
                    _configService.Save(_config);
                    _logger.Info(LogCategory.Settings, $"Đã xóa ứng dụng: {app.Name}");
                }
                return BuildStatePayload();
            }

            case "launchSingleApp":
            {
                string appId = payload.GetProperty("id").GetString() ?? "";
                var app = _config.StartupApps.FirstOrDefault(a => a.Id == appId)
                    ?? throw new InvalidOperationException("Không tìm thấy ứng dụng.");
                var res = await Task.Run(() => _launcher.LaunchSingle(app));
                return new { outcome = res.Outcome.ToString(), res.Detail, appName = app.Name };
            }

            case "runStartupNow":
            {
                await _orchestrator.RunStartupWorkflowAsync(_config, isBootLaunch: false);
                return BuildStatePayload();
            }

            case "saveConditions":
            {
                var cond = JsonSerializer.Deserialize<StartupConditions>(payload.GetRawText(), BridgeJsonOptions)
                    ?? new StartupConditions();
                _config.Conditions = cond;
                _configService.Save(_config);
                _logger.Success(LogCategory.Settings, "Đã lưu điều kiện tự động chạy.");
                return BuildStatePayload();
            }

            case "getCurrentWifi":
            {
                string? ssid = await Task.Run(ConditionEvaluator.GetCurrentWifiSsid);
                return new { ssid };
            }

            case "testBot":
            {
                string token = payload.GetProperty("token").GetString() ?? "";
                return await _telegram.TestBotAsync(token);
            }

            case "discoverChats":
            {
                string token = payload.GetProperty("token").GetString() ?? "";
                var (ok, chats, err) = await _telegram.DiscoverChatIdsAsync(token);
                if (!ok) throw new InvalidOperationException(err);
                return chats;
            }

            case "saveTelegram":
            {
                ApplyTelegramPayload(payload);
                _configService.Save(_config);
                _commandListener.SyncState();
                _onTrayRefresh();
                _logger.Success(LogCategory.Settings, "Đã lưu cấu hình Telegram (Bot Token được mã hóa DPAPI).");
                return BuildStatePayload();
            }

            case "saveWebhook":
            {
                var dto = JsonSerializer.Deserialize<CustomWebhookChannel>(payload.GetRawText(), BridgeJsonOptions)
                    ?? throw new InvalidDataException("Dữ liệu webhook không hợp lệ.");
                if (string.IsNullOrWhiteSpace(dto.Url)) throw new InvalidOperationException("Vui lòng nhập Webhook URL.");
                if (string.IsNullOrWhiteSpace(dto.Name)) dto.Name = dto.ChannelType.ToString();

                int idx = _config.ExtraWebhooks.FindIndex(w => w.Id == dto.Id);
                if (idx >= 0) _config.ExtraWebhooks[idx] = dto;
                else
                {
                    dto.Id = string.IsNullOrWhiteSpace(dto.Id) ? Guid.NewGuid().ToString("N") : dto.Id;
                    _config.ExtraWebhooks.Add(dto);
                }
                _configService.Save(_config);
                return BuildStatePayload();
            }

            case "deleteWebhook":
            {
                string id = payload.GetProperty("id").GetString() ?? "";
                _config.ExtraWebhooks.RemoveAll(w => w.Id == id);
                _configService.Save(_config);
                return BuildStatePayload();
            }

            case "testWebhook":
            {
                string id = payload.GetProperty("id").GetString() ?? "";
                var wh = _config.ExtraWebhooks.FirstOrDefault(w => w.Id == id)
                    ?? throw new InvalidOperationException("Không tìm thấy Webhook.");
                var (ok, detail) = await _webhooks.SendSingleAsync(
                    wh, "Test kết nối", $"[WinPilot] Test Webhook từ {Environment.MachineName}", Array.Empty<string>());
                return new { ok, detail };
            }

            case "testStartupTelegram":
            {
                ApplyTelegramPayload(payload);
                var names = _config.StartupApps
                    .Where(a => a.ProfileId == _config.ActiveProfileId && a.Enabled)
                    .Select(a => a.Name)
                    .ToList();
                var res = await _orchestrator.SendStartupTelegramAsync(_config, names);
                return new { res.AllSucceeded, res.Summary, state = BuildStatePayload() };
            }

            case "testShutdownTelegram":
            {
                ApplyTelegramPayload(payload);
                var res = await Task.Run(() =>
                    _orchestrator.ExecuteShutdownNotificationSync(_config, ShutdownEventKind.Simulated));
                if (res is null)
                {
                    return new { AllSucceeded = false, Summary = "Thông báo tắt máy đang tắt hoặc thiếu Bot Token / Chat ID.", state = BuildStatePayload() };
                }
                return new { res.AllSucceeded, res.Summary, state = BuildStatePayload() };
            }

            case "renderPreview":
            {
                string template = payload.GetProperty("template").GetString() ?? "";
                bool isShutdown = payload.GetProperty("isShutdown").GetBoolean();
                var enabledNames = _config.StartupApps
                    .Where(a => a.ProfileId == _config.ActiveProfileId && a.Enabled)
                    .Select(a => a.Name)
                    .ToList();
                if (enabledNames.Count == 0) enabledNames.Add("Google Chrome (Ví dụ)");
                string rendered = TemplateRenderer.Render(
                    template,
                    new TemplateContext
                    {
                        EventLabel = isShutdown ? "Tắt máy / Khởi động lại" : "Khởi động máy",
                        AppNames = enabledNames
                    },
                    "HTML");
                return new { html = rendered };
            }

            case "saveSettings":
            {
                var gs = JsonSerializer.Deserialize<GeneralSettings>(payload.GetRawText(), BridgeJsonOptions)
                    ?? throw new InvalidDataException("Dữ liệu cài đặt không hợp lệ.");

                _startupManager.SetEnabled(gs.AutoStartWithWindows);
                _config.General.AutoStartWithWindows = gs.AutoStartWithWindows;
                _config.General.StartMinimizedToTray = gs.StartMinimizedToTray;
                _config.General.MinimizeToTrayOnClose = gs.MinimizeToTrayOnClose;
                _config.General.RunStartupTasksOnlyWithStartupArg = gs.RunStartupTasksOnlyWithStartupArg;
                _config.General.StartupDelaySeconds = Math.Clamp(gs.StartupDelaySeconds, 0, 300);
                _config.General.NetworkWaitTimeoutSeconds = Math.Clamp(gs.NetworkWaitTimeoutSeconds, 0, 300);

                _configService.Save(_config);
                _logger.Success(LogCategory.Settings, "Đã lưu cài đặt hệ thống.");
                return BuildStatePayload();
            }

            case "exportConfig":
            {
                var dlg = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "Xuất cấu hình WinPilot",
                    Filter = "JSON (*.json)|*.json",
                    FileName = $"winpilot-backup-{DateTime.Now:yyyyMMdd}.json"
                };
                if (dlg.ShowDialog(this) == true)
                {
                    _configService.ExportToFile(_config, dlg.FileName, includeSecrets: false);
                    return new { exported = true, path = dlg.FileName };
                }
                return new { exported = false };
            }

            case "importConfig":
            {
                var dlg = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "Nhập cấu hình WinPilot",
                    Filter = "JSON (*.json)|*.json"
                };
                if (dlg.ShowDialog(this) == true)
                {
                    var imported = _configService.ImportFromFile(dlg.FileName);
                    _config.General = imported.General;
                    _config.Profiles = imported.Profiles;
                    _config.ActiveProfileId = imported.ActiveProfileId;
                    _config.StartupApps = imported.StartupApps;
                    _config.Conditions = imported.Conditions;
                    _config.ExtraWebhooks = imported.ExtraWebhooks;
                    if (!string.IsNullOrWhiteSpace(imported.Telegram.BotToken))
                    {
                        _config.Telegram.BotToken = imported.Telegram.BotToken;
                    }
                    _config.Telegram.ChatIds = imported.Telegram.ChatIds;
                    _config.Telegram.StartupTemplate = imported.Telegram.StartupTemplate;
                    _config.Telegram.ShutdownTemplate = imported.Telegram.ShutdownTemplate;
                    _config.Telegram.NotifyOnStartup = imported.Telegram.NotifyOnStartup;
                    _config.Telegram.NotifyOnShutdown = imported.Telegram.NotifyOnShutdown;
                    _config.Telegram.NotifyOnLogoff = imported.Telegram.NotifyOnLogoff;
                    _config.Telegram.NotifyOnSleep = imported.Telegram.NotifyOnSleep;
                    _config.Telegram.NotifyOnWake = imported.Telegram.NotifyOnWake;
                    _config.Telegram.NotifyOnLock = imported.Telegram.NotifyOnLock;
                    _config.Telegram.NotifyOnUnlock = imported.Telegram.NotifyOnUnlock;
                    _config.Telegram.EnableRemoteCommands = imported.Telegram.EnableRemoteCommands;
                    _config.Telegram.AllowedCommandChatIds = imported.Telegram.AllowedCommandChatIds;

                    _configService.Save(_config);
                    _commandListener.SyncState();
                    _onTrayRefresh();
                    _logger.Success(LogCategory.Settings, "Đã nhập cấu hình từ tệp.");
                    return new { imported = true, state = BuildStatePayload() };
                }
                return new { imported = false };
            }

            case "openLogsFolder":
                Directory.CreateDirectory(_logger.LogDirectory);
                Process.Start(new ProcessStartInfo { FileName = _logger.LogDirectory, UseShellExecute = true });
                return null;

            case "openDataFolder":
                Directory.CreateDirectory(_configService.ConfigDirectory);
                Process.Start(new ProcessStartInfo { FileName = _configService.ConfigDirectory, UseShellExecute = true });
                return null;

            case "clearLogs":
                _logger.Clear();
                return GetLogsSnapshot();

            default:
                throw new NotSupportedException($"Hành động không hỗ trợ: {action}");
        }
    }

    private void ApplyTelegramPayload(JsonElement payload)
    {
        if (payload.TryGetProperty("enabled", out var en)) _config.Telegram.Enabled = en.GetBoolean();
        if (payload.TryGetProperty("botToken", out var tok)) _config.Telegram.BotToken = tok.GetString() ?? "";
        if (payload.TryGetProperty("chatIds", out var cids)) _config.Telegram.ChatIds = cids.GetString() ?? "";
        if (payload.TryGetProperty("notifyOnStartup", out var nos)) _config.Telegram.NotifyOnStartup = nos.GetBoolean();
        if (payload.TryGetProperty("startupSendMode", out var mode) &&
            Enum.TryParse<StartupNotificationMode>(mode.GetString(), true, out var parsedMode))
        {
            _config.Telegram.StartupSendMode = parsedMode;
        }
        if (payload.TryGetProperty("startupTemplate", out var st)) _config.Telegram.StartupTemplate = st.GetString() ?? "";
        if (payload.TryGetProperty("notifyOnShutdown", out var nosd)) _config.Telegram.NotifyOnShutdown = nosd.GetBoolean();
        if (payload.TryGetProperty("notifyOnLogoff", out var nol)) _config.Telegram.NotifyOnLogoff = nol.GetBoolean();
        if (payload.TryGetProperty("notifyOnSleep", out var nosl)) _config.Telegram.NotifyOnSleep = nosl.GetBoolean();
        if (payload.TryGetProperty("notifyOnWake", out var nowk)) _config.Telegram.NotifyOnWake = nowk.GetBoolean();
        if (payload.TryGetProperty("notifyOnLock", out var nolk)) _config.Telegram.NotifyOnLock = nolk.GetBoolean();
        if (payload.TryGetProperty("notifyOnUnlock", out var noul)) _config.Telegram.NotifyOnUnlock = noul.GetBoolean();
        if (payload.TryGetProperty("shutdownTimeoutMs", out var ms)) _config.Telegram.ShutdownTimeoutMs = Math.Clamp(ms.GetInt32(), 1000, 4500);
        if (payload.TryGetProperty("shutdownTemplate", out var sdt)) _config.Telegram.ShutdownTemplate = sdt.GetString() ?? "";
        if (payload.TryGetProperty("enableRemoteCommands", out var erc)) _config.Telegram.EnableRemoteCommands = erc.GetBoolean();
        if (payload.TryGetProperty("allowedCommandChatIds", out var acc)) _config.Telegram.AllowedCommandChatIds = acc.GetString() ?? "";
    }

    private object BuildStatePayload()
    {
        bool regEnabled = _startupManager.IsEnabled();
        _config.General.AutoStartWithWindows = regEnabled;

        var apps = _config.StartupApps
            .Where(a => a.ProfileId == _config.ActiveProfileId)
            .OrderBy(a => a.Order)
            .Select(a => new
            {
                a.Id,
                a.ProfileId,
                a.Name,
                a.Path,
                a.Arguments,
                a.WorkingDirectory,
                a.DelaySeconds,
                windowMode = a.WindowMode.ToString(),
                a.RunAsAdmin,
                a.SkipIfAlreadyRunning,
                a.Enabled,
                a.Order,
                a.Notes,
                exists = AppLauncherService.TargetExists(a.Path),
                iconDataUrl = IconExtractor.GetBase64PngIcon(a.Path)
            })
            .ToList();

        return new
        {
            deviceName = Environment.MachineName,
            userName = Environment.UserName,
            localIp = TemplateRenderer.GetLocalIPv4(),
            registryEnabled = regEnabled,
            registryCommand = _startupManager.GetRegisteredCommand(),
            configPath = _configService.ConfigFilePath,
            logDirectory = _logger.LogDirectory,
            activeProfileId = _config.ActiveProfileId,
            profiles = _config.Profiles,
            conditions = _config.Conditions,
            extraWebhooks = _config.ExtraWebhooks,
            general = _config.General,
            apps,
            telegram = new
            {
                _config.Telegram.Enabled,
                botToken = _config.Telegram.BotToken,
                _config.Telegram.ChatIds,
                _config.Telegram.NotifyOnStartup,
                startupSendMode = _config.Telegram.StartupSendMode.ToString(),
                _config.Telegram.StartupTemplate,
                _config.Telegram.NotifyOnShutdown,
                _config.Telegram.NotifyOnLogoff,
                _config.Telegram.NotifyOnSleep,
                _config.Telegram.NotifyOnWake,
                _config.Telegram.NotifyOnLock,
                _config.Telegram.NotifyOnUnlock,
                _config.Telegram.ShutdownTimeoutMs,
                _config.Telegram.ShutdownTemplate,
                _config.Telegram.EnableRemoteCommands,
                _config.Telegram.AllowedCommandChatIds,
                isConfigured = _config.Telegram.Enabled &&
                               !string.IsNullOrWhiteSpace(_config.Telegram.BotToken) &&
                               TelegramNotificationService.ParseChatIds(_config.Telegram.ChatIds).Count > 0
            },
            stats = new
            {
                _config.Stats.LastStartupSummary,
                _config.Stats.LastTelegramStatus,
                lastShutdownStatus = _orchestrator.LastShutdownStatus
            },
            logs = GetLogsSnapshot()
        };
    }

    private object GetLogsSnapshot() =>
        _logger.GetSnapshot()
            .Reverse()
            .Select(l => new
            {
                time = l.FormattedTimestamp,
                dateTime = l.FormattedDateTime,
                level = l.Level.ToString(),
                category = l.Category.ToString(),
                message = l.Message
            })
            .ToList();

    private void Reply(string id, bool ok, object? data, string? error)
    {
        if (!_webReady) return;
        string json = JsonSerializer.Serialize(new { id, ok, data, error }, BridgeJsonOptions);
        Dispatcher.InvokeAsync(() => WebView.CoreWebView2.PostWebMessageAsJson(json));
    }

    private void PushEvent(string eventName, object? data)
    {
        if (!_webReady) return;
        string json = JsonSerializer.Serialize(new { @event = eventName, data }, BridgeJsonOptions);
        Dispatcher.InvokeAsync(() => WebView.CoreWebView2.PostWebMessageAsJson(json));
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_isExitingProvider() && _config.General.MinimizeToTrayOnClose)
        {
            e.Cancel = true;
            Hide();
        }
    }

    private sealed record InstalledAppAddDto(string Name, string FilePath);
}
