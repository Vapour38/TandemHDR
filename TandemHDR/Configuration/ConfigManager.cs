using System.IO;
using System.Text.Json;
using TandemHdr.Services;

namespace TandemHdr.Configuration;

internal static class ConfigManager
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    public static string ConfigPath
    {
        get
        {
            string dir = Path.GetDirectoryName(Environment.ProcessPath ?? Application.ExecutablePath)
                         ?? Environment.CurrentDirectory;
            return Path.Combine(dir, "config.json");
        }
    }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Options);
                if (loaded != null)
                {
                    Logger.Log($"Config loaded from {ConfigPath}");
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to load config, using defaults: {ex.Message}");
        }

        var config = new AppConfig();
        Save(config);
        Logger.Log($"Created default config at {ConfigPath}");
        return config;
    }

    public static void Save(AppConfig config)
    {
        try
        {
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(config, Options));
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to save config: {ex.Message}");
        }
    }
}
