using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using TandemHdr.Services;

namespace TandemHdr.Configuration;

internal static class ConfigManager
{
    private const string RegistryKey = @"SOFTWARE\Tandem HDR";
    private const string ValueName = "Config";

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    /// <summary>Settings live in the registry rather than beside the exe, so a fresh copy
    /// can be run from anywhere without leaving a file behind.</summary>
    public static AppConfig Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryKey, false);
            if (key?.GetValue(ValueName) is string json && json.Length > 0)
            {
                var loaded = JsonSerializer.Deserialize<AppConfig>(json, Options);
                if (loaded != null)
                {
                    Logger.Log("Config loaded from the registry");
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to load config, using defaults: {ex.Message}");
        }

        var config = ImportLegacyConfig() ?? new AppConfig();
        Save(config);
        return config;
    }

    public static void Save(AppConfig config)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryKey);
            key.SetValue(ValueName, JsonSerializer.Serialize(config, Options), RegistryValueKind.String);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to save config: {ex.Message}");
        }
    }

    /// <summary>Picks up the config.json that older versions kept beside the exe, so an
    /// update does not silently reset a working setup. The file is left in place; the
    /// registry copy is authoritative from here on.</summary>
    private static AppConfig? ImportLegacyConfig()
    {
        try
        {
            string dir = Path.GetDirectoryName(Environment.ProcessPath ?? Application.ExecutablePath)
                         ?? Environment.CurrentDirectory;
            string path = Path.Combine(dir, "config.json");
            if (!File.Exists(path)) return null;

            var loaded = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options);
            if (loaded != null) Logger.Log($"Imported settings from {path}");
            return loaded;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to import the old config.json: {ex.Message}");
            return null;
        }
    }
}
