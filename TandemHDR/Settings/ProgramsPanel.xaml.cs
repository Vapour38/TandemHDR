using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using TandemHdr.Configuration;
using TandemHdr.Services;
// WinForms and WPF are both enabled in this project and share several type names.
using Button = System.Windows.Controls.Button;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;
using UserControl = System.Windows.Controls.UserControl;

namespace TandemHdr.Settings;

/// <summary>The Programs tab: the list of programs that switch HDR, and the choices for each.
/// SettingsWindow owns the config; this panel edits <see cref="AppConfig.Programs"/> and tells
/// the tray when the watched set changes.</summary>
internal partial class ProgramsPanel : UserControl
{
    private readonly ObservableCollection<ProgramRow> _rows = [];

    /// <summary>When the library scan last played each program. Kept in memory only.</summary>
    private readonly Dictionary<string, DateTime?> _lastPlayed = new(StringComparer.OrdinalIgnoreCase);

    private AppConfig _config = null!;
    private Action _onProgramsChanged = null!;
    private bool _readOnly;
    private bool _initializing = true;
    private bool _scanStarted;

    public ProgramsPanel()
    {
        InitializeComponent();
        ProgramsList.ItemsSource = _rows;
        Loaded += async (_, _) => await FillNamesAsync();
    }

    /// <summary>Call once. A read-only panel never scans the libraries or saves the config;
    /// the screenshot probe uses it so that it cannot touch the saved settings.</summary>
    public void Initialize(AppConfig config, Action onProgramsChanged, bool readOnly = false)
    {
        _config = config;
        _onProgramsChanged = onProgramsChanged;
        _readOnly = readOnly;

        AutoSwitchCheck.IsChecked = config.AutoSwitchForPrograms;
        Rebuild();
        _initializing = false;
    }

    /// <summary>Reads the auto-switch setting again after the tray menu changed it. The
    /// config is already saved, so the toggle handler must not run.</summary>
    public void SyncAutoSwitch()
    {
        _initializing = true;
        AutoSwitchCheck.IsChecked = _config.AutoSwitchForPrograms;
        _initializing = false;
    }

    /// <summary>Opens the first rows. Used by the screenshot probe.</summary>
    internal void ExpandFirst(int count)
    {
        foreach (var row in _rows.Take(count))
            row.IsExpanded = true;
    }

    /// <summary>Rebuilds the sorted list from the config. Open rows stay open.</summary>
    private void Rebuild()
    {
        var open = _rows.Where(r => r.IsExpanded).Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var installed = IccProfileService.ListInstalledProfiles();

        var rows = _config.Programs
            .Select(p => new ProgramRow(p, _lastPlayed.GetValueOrDefault(p.Path), installed, OnRowEdited)
            {
                IsExpanded = open.Contains(p.Path),
            })
            .OrderBy(r => r.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        _rows.Clear();
        foreach (var row in rows)
            _rows.Add(row);

        NoProgramsText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Saves the config. Skipped in read-only mode.</summary>
    private void Save()
    {
        if (!_readOnly)
            ConfigManager.Save(_config);
    }

    private void OnRowEdited()
    {
        Save();
        _onProgramsChanged();
    }

    /// <summary>Gives names to programs that have none, once, when the panel first shows.
    /// Names do not change what is watched, so the tray is not told.</summary>
    private async Task FillNamesAsync()
    {
        if (_scanStarted || _readOnly) return;
        _scanStarted = true;

        try
        {
            var games = await GameScanner.ScanAsync();
            bool changed = false;

            foreach (var program in _config.Programs)
            {
                var match = games.FirstOrDefault(g => Same(g.ExecutablePath, program.Path))
                            ?? games.FirstOrDefault(g => Same(
                                Path.GetDirectoryName(g.ExecutablePath), Path.GetDirectoryName(program.Path)));
                if (match == null) continue;

                _lastPlayed[program.Path] = match.LastPlayed;
                if (string.IsNullOrWhiteSpace(program.Name))
                {
                    program.Name = match.Name;
                    program.Source = match.Source;
                    changed = true;
                }
            }

            if (changed) Save();
            Rebuild();
        }
        catch (Exception ex)
        {
            Logger.Log($"Could not read game libraries for program names: {ex.Message}");
        }
    }

    private static bool Same(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private void OnAddProgramClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a program",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
            return;

        AddProgram(new WatchedProgram { Path = dialog.FileName });
        Rebuild();
        OnRowEdited();
    }

    private void OnScanGamesClick(object sender, RoutedEventArgs e)
    {
        var picker = new GamePickerWindow(_config.Programs.Select(p => p.Path)) { Owner = Window.GetWindow(this) };
        if (picker.ShowDialog() != true || picker.SelectedEntries.Count == 0)
            return;

        foreach (var entry in picker.SelectedEntries)
        {
            // The Recently run tab has no source, and its name is only the exe name.
            bool named = !string.IsNullOrEmpty(entry.Source);
            AddProgram(new WatchedProgram
            {
                Path = entry.Path,
                Name = named ? entry.Name : null,
                Source = named ? entry.Source : null,
            });
        }

        Rebuild();
        OnRowEdited();
    }

    /// <summary>Adds one program to the config and ignores duplicates. The caller saves, so
    /// a batch add writes the file once.</summary>
    private void AddProgram(WatchedProgram program)
    {
        if (_config.Programs.Any(p => Same(p.Path, program.Path)))
            return;

        _config.Programs.Add(program);
    }

    private void OnRemoveProgramClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is not ProgramRow row)
            return;

        _config.Programs.Remove(row.Program);
        Rebuild();
        OnRowEdited();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (((Button)sender).DataContext is not ProgramRow row)
            return;

        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.Path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Logger.Log($"Could not open the folder of '{row.Path}': {ex.Message}");
        }
    }

    /// <summary>Space toggles a ToggleButton already. Enter does the same here.</summary>
    private void OnHeaderKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;

        var header = (System.Windows.Controls.Primitives.ToggleButton)sender;
        header.IsChecked = header.IsChecked != true;
        e.Handled = true;
    }

    private void OnAutoSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        _config.AutoSwitchForPrograms = AutoSwitchCheck.IsChecked == true;
        OnRowEdited();
    }
}
