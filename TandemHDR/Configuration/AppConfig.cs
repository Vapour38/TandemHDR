namespace TandemHdr.Configuration;

internal class AppConfig
{
    public string? SdrProfilePath { get; set; }
    public string? HdrProfilePath { get; set; }
    public bool SyncProfilesWithExternalHdrChanges { get; set; } = true;
    public bool StartWithWindows { get; set; } = true;
    public bool ShowNotifications { get; set; } = true;
    public bool CheckForUpdatesOnStart { get; set; } = true;

    /// <summary>Full paths of programs that should force HDR on while they are running.
    /// Matching is by executable file name, so a game that relaunches itself from a
    /// different directory (or through a launcher stub) is still recognised.</summary>
    public List<string> HdrPrograms { get; set; } = [];

    /// <summary>Master switch for the program watcher, so the list can be kept without
    /// being acted on.</summary>
    public bool AutoSwitchForPrograms { get; set; } = true;
}
