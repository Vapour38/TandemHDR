using System.ComponentModel;
using System.IO;
using TandemHdr.Configuration;
using TandemHdr.Services;

namespace TandemHdr.Settings;

/// <summary>One option in a row's HDR profile dropdown. A null Path means "use the main
/// HDR profile".</summary>
internal sealed record ProgramProfileChoice(string Display, string? Path)
{
    public override string ToString() => Display;
}

/// <summary>The view-model of one row on the Programs tab. It wraps the config entry, so
/// an edit writes straight to the config and then asks the panel to save. The panel
/// rebuilds the rows after a name changes, so only the values that the row edits itself
/// raise change events.</summary>
internal sealed class ProgramRow : INotifyPropertyChanged
{
    private const string MainProfileText = "Same as the main HDR profile";

    private readonly Action _edited;
    private bool _isExpanded;
    private ProgramProfileChoice? _selectedProfile;

    public ProgramRow(WatchedProgram program, DateTime? lastPlayed, IReadOnlyList<string> installedProfiles, Action edited)
    {
        Program = program;
        LastPlayed = lastPlayed;
        _edited = edited;
        DisplayName = ProgramNames.DisplayName(program);
        FileMissing = !File.Exists(program.Path);
        Profiles = BuildProfiles(program, installedProfiles);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public WatchedProgram Program { get; }
    public string Path => Program.Path;
    public string ExeName => System.IO.Path.GetFileName(Program.Path);
    public string DisplayName { get; }

    /// <summary>Known only when the library scan found the program. It is not saved.</summary>
    public DateTime? LastPlayed { get; }

    public bool FileMissing { get; }
    public bool FileFound => !FileMissing;
    public bool HasSource => !string.IsNullOrWhiteSpace(Program.Source);
    public string FoundIn => HasSource ? Program.Source! : "Added by hand";
    public bool HasLastPlayed => LastPlayed != null;
    public string LastPlayedText => LastPlayed?.ToString("d MMMM yyyy") ?? string.Empty;
    public bool IsOff => !Program.Enabled;
    public bool HasOwnProfile => !string.IsNullOrWhiteSpace(Program.HdrProfilePath);

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            Raise(nameof(IsExpanded));
        }
    }

    public bool Enabled
    {
        get => Program.Enabled;
        set
        {
            if (Program.Enabled == value) return;
            Program.Enabled = value;
            Raise(nameof(Enabled));
            Raise(nameof(IsOff));
            _edited();
        }
    }

    public IReadOnlyList<ProgramProfileChoice> Profiles { get; }

    public ProgramProfileChoice SelectedProfile
    {
        get => _selectedProfile ??= Profiles.FirstOrDefault(p => Same(p.Path, Program.HdrProfilePath)) ?? Profiles[0];
        set
        {
            // The ComboBox writes null when its items are reset.
            if (value == null || ReferenceEquals(value, _selectedProfile)) return;
            _selectedProfile = value;
            Program.HdrProfilePath = value.Path;
            Raise(nameof(SelectedProfile));
            Raise(nameof(HasOwnProfile));
            _edited();
        }
    }

    private static List<ProgramProfileChoice> BuildProfiles(WatchedProgram program, IReadOnlyList<string> installed)
    {
        var choices = new List<ProgramProfileChoice> { new(MainProfileText, null) };
        choices.AddRange(installed.Select(n =>
            new ProgramProfileChoice(n, System.IO.Path.Combine(IccProfileService.SystemColorDir, n))));

        // A stored profile that Windows no longer lists must still show, not read as "main".
        string? stored = program.HdrProfilePath;
        if (!string.IsNullOrWhiteSpace(stored) && !choices.Any(c => Same(c.Path, stored)))
            choices.Insert(1, new ProgramProfileChoice(System.IO.Path.GetFileName(stored), stored));
        return choices;
    }

    private static bool Same(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
