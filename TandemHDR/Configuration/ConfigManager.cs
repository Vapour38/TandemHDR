using System.IO;
using System.Text.Json;
using Microsoft.Win32;
using TandemHdr.Services;

namespace TandemHdr.Configuration;

internal static class ConfigManager
{
    private const string RegistryKey = @"SOFTWARE\Tandem HDR" + BuildInfo.Suffix;
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
                    Normalise(loaded);
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

    /// <summary>Removes the key the settings live in. Only the remove-data flow calls this:
    /// <see cref="Save"/> recreates the key on the next change, so the app has to be on its
    /// way out for the deletion to stick.</summary>
    public static void Delete()
    {
        try
        {
            Registry.CurrentUser.DeleteSubKeyTree(RegistryKey, throwOnMissingSubKey: false);
            Logger.Log("Removed the settings key");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to remove the settings key: {ex.Message}");
        }
    }

    /// <summary>Moves the old bare path list into <see cref="AppConfig.Programs"/>, and
    /// repairs what hand-edited or older JSON can leave behind: an explicit null overrides
    /// the initialiser and would crash the watcher on every launch.</summary>
    private static void Normalise(AppConfig config)
    {
        config.Programs ??= [];
        foreach (string path in config.HdrPrograms ?? [])
        {
            if (!config.Programs.Any(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase)))
                config.Programs.Add(new WatchedProgram { Path = path });
        }
        config.HdrPrograms = null;
        config.Programs.RemoveAll(p => p == null || string.IsNullOrWhiteSpace(p.Path));
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
            if (loaded != null)
            {
                Normalise(loaded);
                Logger.Log($"Imported settings from {path}");
            }
            return loaded;
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to import the old config.json: {ex.Message}");
            return null;
        }
    }
}
