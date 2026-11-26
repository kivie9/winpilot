using System.Text.Json;
using System.Text.Json.Serialization;
using WinPilot.Core.Models;
using WinPilot.Core.Security;

namespace WinPilot.Core.Services;

/// <summary>
/// Persists AppConfig atomically in AppData and protects secrets with DPAPI.
/// </summary>
public sealed class ConfigService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ISecretProtector _protector;
    private readonly object _fileLock = new();

    public string ConfigFilePath { get; }
    public string ConfigDirectory => Path.GetDirectoryName(ConfigFilePath)!;

    public ConfigService(string? configFilePath = null, ISecretProtector? protector = null)
    {
        _protector = protector ?? new DpapiSecretProtector();
        ConfigFilePath = configFilePath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "WinPilot",
            "config.json");
    }

    public AppConfig Load()
    {
        lock (_fileLock)
        {
            if (!File.Exists(ConfigFilePath))
            {
                return new AppConfig();
            }

            try
            {
                string json = File.ReadAllText(ConfigFilePath);
                var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
                config.General ??= new GeneralSettings();
                config.Profiles ??= new List<AppProfile>();
                if (config.Profiles.Count == 0)
                {
                    config.Profiles.Add(new AppProfile { Id = "default", Name = "Mặc định" });
                }
                if (string.IsNullOrWhiteSpace(config.ActiveProfileId) ||
                    !config.Profiles.Any(p => p.Id == config.ActiveProfileId))
                {
                    config.ActiveProfileId = config.Profiles[0].Id;
                }
                config.StartupApps ??= new List<StartupAppEntry>();
                foreach (var app in config.StartupApps)
                {
                    if (string.IsNullOrWhiteSpace(app.ProfileId)) app.ProfileId = "default";
                }
                config.Conditions ??= new StartupConditions();
                config.Telegram ??= new TelegramSettings();
                config.ExtraWebhooks ??= new List<CustomWebhookChannel>();
                config.Stats ??= new RuntimeStats();

                config.Telegram.BotToken = _protector.Unprotect(config.Telegram.EncryptedBotToken);
                NormalizeOrder(config.StartupApps);
                return config;
            }
            catch
            {
                TryBackupCorruptConfig();
                return new AppConfig();
            }
        }
    }

    public void Save(AppConfig config)
    {
        lock (_fileLock)
        {
            Directory.CreateDirectory(ConfigDirectory);
            NormalizeOrder(config.StartupApps);
            config.Telegram.EncryptedBotToken = _protector.Protect(config.Telegram.BotToken.Trim());

            string json = JsonSerializer.Serialize(config, JsonOptions);
            string tempPath = ConfigFilePath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, ConfigFilePath, overwrite: true);
        }
    }

    public void ExportToFile(AppConfig config, string destinationPath, bool includeSecrets = false)
    {
        var clone = JsonSerializer.Deserialize<AppConfig>(
            JsonSerializer.Serialize(config, JsonOptions), JsonOptions) ?? new AppConfig();

        if (!includeSecrets)
        {
            clone.Telegram.EncryptedBotToken = string.Empty;
        }
        else
        {
            clone.Telegram.EncryptedBotToken = _protector.Protect(config.Telegram.BotToken.Trim());
        }

        string json = JsonSerializer.Serialize(clone, JsonOptions);
        File.WriteAllText(destinationPath, json);
    }

    public AppConfig ImportFromFile(string sourcePath)
    {
        string json = File.ReadAllText(sourcePath);
        var imported = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
            ?? throw new InvalidDataException("File cấu hình không hợp lệ.");

        imported.General ??= new GeneralSettings();
        imported.StartupApps ??= new List<StartupAppEntry>();
        imported.Telegram ??= new TelegramSettings();
        imported.ExtraWebhooks ??= new List<CustomWebhookChannel>();
        imported.Stats ??= new RuntimeStats();

        imported.Telegram.BotToken = _protector.Unprotect(imported.Telegram.EncryptedBotToken);
        NormalizeOrder(imported.StartupApps);
        return imported;
    }

    private static void NormalizeOrder(List<StartupAppEntry> apps)
    {
        for (int i = 0; i < apps.Count; i++)
        {
            apps[i].Order = i;
        }
    }

    private void TryBackupCorruptConfig()
    {
        try
        {
            string backupPath = ConfigFilePath + $".corrupt-{DateTime.Now:yyyyMMddHHmmss}";
            File.Copy(ConfigFilePath, backupPath, overwrite: true);
        }
        catch
        {
            // Best-effort backup only.
        }
    }
}
