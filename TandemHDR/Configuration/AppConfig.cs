using System.Text.Json.Serialization;

namespace TandemHdr.Configuration;

internal class AppConfig
{
    public string? SdrProfilePath { get; set; }
    public string? HdrProfilePath { get; set; }
    public bool SyncProfilesWithExternalHdrChanges { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool CheckForUpdatesOnStart { get; set; } = true;

    /// <summary>A release the user asked not to be told about again. Suppresses the startup
    /// balloon only — the settings window still offers it.</summary>
    public string? SkippedVersion { get; set; }

    /// <summary>Programs that force HDR on while they are running. The file name selects
    /// the candidate, so a game that relaunches itself from a different directory (or
    /// through a launcher stub) is still recognised; the path only rules out a different
    /// program that happens to share the name.</summary>
    public List<WatchedProgram> Programs { get; set; } = [];

    /// <summary>The bare path list that versions up to 0.5.2 saved. Read once by
    /// <see cref="ConfigManager"/> to fill <see cref="Programs"/>, and not written again.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? HdrPrograms { get; set; }

    /// <summary>Master switch for the program watcher, so the list can be kept without
    /// being acted on.</summary>
    public bool AutoSwitchForPrograms { get; set; } = true;
}

/// <summary>One entry of the program list.</summary>
internal class WatchedProgram
{
    public string Path { get; set; } = string.Empty;

    /// <summary>The title from the launcher that found it, or null when it was added by
    /// hand or before names were kept. See <see cref="Services.ProgramNames"/>.</summary>
    public string? Name { get; set; }

    /// <summary>The launcher it came from ("Steam", "Epic" and so on), or null.</summary>
    public string? Source { get; set; }

    /// <summary>Off keeps the entry on the list without letting it switch HDR.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Applied in place of the main HDR profile while this program runs. Null
    /// means the main one.</summary>
    public string? HdrProfilePath { get; set; }
}
