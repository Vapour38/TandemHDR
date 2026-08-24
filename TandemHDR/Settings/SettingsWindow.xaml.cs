using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TandemHdr.Configuration;
using TandemHdr.Native;
using TandemHdr.Services;
using Microsoft.Win32;
// WinForms and WPF are both enabled in this project and share several type names.
using Brush = System.Windows.Media.Brush;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace TandemHdr.Settings;

internal partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly IccProfileService _iccService;
    private readonly Func<HdrState> _getCurrentState;
    private readonly Func<string?> _getActiveProfileName;
    private readonly Action _onIntervalsChanged;
    private readonly Action _onProfileChanged;
    private readonly Action _onProgramsChanged;
    private readonly DispatcherTimer _statusTimer;

    private bool _initializing = true;

    private readonly ObservableCollection<ProgramEntry> _programs = [];

    /// <summary>One row of the Programs tab. Name is what the user reads; Path is both the
    /// subtitle and the identity used to remove the entry.</summary>
    internal record ProgramEntry(string Name, string Path);

    public SettingsWindow(
        AppConfig config,
        DisplayService displayService,
        HdrService hdrService,
        IccProfileService iccService,
        Func<HdrState> getCurrentState,
        Func<string?> getActiveProfileName,
        Action onIntervalsChanged,
        Action onProfileChanged,
        Action onProgramsChanged)
    {
        InitializeComponent();

        _config = config;
        _iccService = iccService;
        _getCurrentState = getCurrentState;
        _getActiveProfileName = getActiveProfileName;
        _onIntervalsChanged = onIntervalsChanged;
        _onProfileChanged = onProfileChanged;
        _onProgramsChanged = onProgramsChanged;

        // Set after InitializeComponent, not via IsChecked="True" in XAML: the Checked event
        // fires while BAML is still building the tree, before the panel fields it touches
        // have been assigned.
        ProfilesToggle.IsChecked = true;
        SystemToggle.IsChecked = true;
        TimingToggle.IsChecked = true;

        ProgramsList.ItemsSource = _programs;

        LoadFromConfig();
        RefreshStatus();

        // Keeps the status readout live (e.g. the user flips HDR from Windows Settings
        // while this window is open) without coupling to the tray's own poll timer.
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _statusTimer.Tick += (_, _) => RefreshStatus();
        _statusTimer.Start();

        Closed += (_, _) => _statusTimer.Stop();
        _initializing = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        var hwnd = new WindowInteropHelper(this).Handle;
        // No SetDarkTitleBar here: the caption is drawn by the app now, so the immersive
        // dark-mode attribute has no caption left to colour.
        DwmApi.SetRoundedCorners(hwnd);

        // Acrylic is the glassier of the two system backdrops; Mica is the fallback for
        // builds that reject it, and a flat brush for anything older than 22H2. When a
        // backdrop is live the window takes a translucent slate wash rather than a solid
        // fill, so the palette reads slate-blue without painting the glass out.
        bool backdropApplied = DwmApi.TrySetAcrylicBackdrop(hwnd) || DwmApi.TrySetMicaBackdrop(hwnd);
        RootGrid.Background = (Brush)FindResource(backdropApplied ? "WindowTintBrush" : "WindowBackgroundBrush");
    }

    private void LoadFromConfig()
    {
        SdrPathText.Text = DisplayPath(_config.SdrProfilePath);
        HdrPathText.Text = DisplayPath(_config.HdrProfilePath);
        UpdateProfileWarning(SdrWarningText, _config.SdrProfilePath);
        UpdateProfileWarning(HdrWarningText, _config.HdrProfilePath);

        AutoStartCheck.IsChecked = AutoStartService.IsEnabled();
        SyncExternalCheck.IsChecked = _config.SyncProfilesWithExternalHdrChanges;
        AutoSwitchCheck.IsChecked = _config.AutoSwitchForPrograms;

        _programs.Clear();
        foreach (var path in _config.HdrPrograms)
            _programs.Add(new ProgramEntry(Path.GetFileNameWithoutExtension(path), path));
        UpdateProgramsEmptyState();
        StateCheckStepper.Value = _config.HdrStateCheckIntervalSeconds;
        ProfileRefreshStepper.Value = _config.ProfileRefreshIntervalSeconds;
    }

    private static string DisplayPath(string? path)
        => string.IsNullOrWhiteSpace(path) ? "Not set" : Path.GetFileName(path);

    private void UpdateProfileWarning(System.Windows.Controls.TextBlock warningText, string? path)
    {
        if (!string.IsNullOrWhiteSpace(path) && !IccProfileService.ProfileExists(path))
        {
            warningText.Text = "This profile can no longer be found.";
            warningText.Visibility = Visibility.Visible;
        }
        else
        {
            warningText.Visibility = Visibility.Collapsed;
        }
    }

    private void RefreshStatus()
    {
        var state = _getCurrentState();
        var (label, dotBrushKey) = state switch
        {
            HdrState.On => ("HDR is on", "AccentBrush"),
            HdrState.Off => ("HDR is off", "NeutralDotBrush"),
            _ => ("HDR is unsupported", "WarningDotBrush"),
        };

        StatusText.Text = label;
        StatusDot.Fill = (Brush)FindResource(dotBrushKey);

        string? active = _getActiveProfileName();
        StatusDetailText.Text = active != null ? $"· Applied ‘{active}’" : string.Empty;
    }

    private void OnSdrBrowseClick(object sender, RoutedEventArgs e)
        => BrowseForProfile(isSdr: true);

    private void OnHdrBrowseClick(object sender, RoutedEventArgs e)
        => BrowseForProfile(isSdr: false);

    private void BrowseForProfile(bool isSdr)
    {
        string? current = isSdr ? _config.SdrProfilePath : _config.HdrProfilePath;
        var dialog = new OpenFileDialog
        {
            Title = isSdr ? "Choose SDR color profile" : "Choose HDR color profile",
            Filter = "Color profiles (*.icc;*.icm)|*.icc;*.icm|All files (*.*)|*.*",
            InitialDirectory = ResolveInitialDirectory(current),
        };

        if (dialog.ShowDialog(this) != true)
            return;

        if (isSdr)
        {
            _config.SdrProfilePath = dialog.FileName;
            SdrPathText.Text = DisplayPath(dialog.FileName);
            UpdateProfileWarning(SdrWarningText, dialog.FileName);
        }
        else
        {
            _config.HdrProfilePath = dialog.FileName;
            HdrPathText.Text = DisplayPath(dialog.FileName);
            UpdateProfileWarning(HdrWarningText, dialog.FileName);
        }

        ConfigManager.Save(_config);
        _onProfileChanged();
        RefreshStatus();
    }

    private static string ResolveInitialDirectory(string? existingPath)
    {
        if (!string.IsNullOrWhiteSpace(existingPath))
        {
            string? dir = Path.GetDirectoryName(existingPath);
            if (dir != null && Directory.Exists(dir))
                return dir;
        }

        return Directory.Exists(IccProfileService.SystemColorDir) ? IccProfileService.SystemColorDir : string.Empty;
    }

    private void OnAutoStartToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        bool enabled = AutoStartCheck.IsChecked == true;
        AutoStartService.SetEnabled(enabled);
        _config.StartWithWindows = enabled;
        ConfigManager.Save(_config);
    }

    private void OnSyncExternalToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        _config.SyncProfilesWithExternalHdrChanges = SyncExternalCheck.IsChecked == true;
        ConfigManager.Save(_config);
    }

    private void OnIntervalCommitted(object? sender, EventArgs e)
    {
        if (_initializing) return;

        _config.HdrStateCheckIntervalSeconds = Math.Max(StateCheckStepper.Value, 1);
        _config.ProfileRefreshIntervalSeconds = ProfileRefreshStepper.Value;
        ConfigManager.Save(_config);
        _onIntervalsChanged();
    }

    private void OnMinimiseClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximiseClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnAddProgramClick(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Choose a program",
            Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
        };

        if (dialog.ShowDialog(this) != true)
            return;

        AddProgram(dialog.FileName);
        ConfigManager.Save(_config);
        _onProgramsChanged();
    }

    private void OnScanGamesClick(object sender, RoutedEventArgs e)
    {
        var picker = new GamePickerWindow(_config.HdrPrograms) { Owner = this };
        if (picker.ShowDialog() != true || picker.SelectedPaths.Count == 0)
            return;

        foreach (string path in picker.SelectedPaths)
            AddProgram(path);

        ConfigManager.Save(_config);
        _onProgramsChanged();
    }

    /// <summary>Adds one program to the config and the visible list, ignoring duplicates.
    /// Saving is left to the caller so a batch add writes the file once.</summary>
    private void AddProgram(string path)
    {
        if (_config.HdrPrograms.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            return;

        _config.HdrPrograms.Add(path);
        _programs.Add(new ProgramEntry(Path.GetFileNameWithoutExtension(path), path));
        UpdateProgramsEmptyState();
    }

    private void OnRemoveProgramClick(object sender, RoutedEventArgs e)
    {
        if (((System.Windows.Controls.Button)sender).Tag is not string path)
            return;

        _config.HdrPrograms.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));

        var row = _programs.FirstOrDefault(p => string.Equals(p.Path, path, StringComparison.OrdinalIgnoreCase));
        if (row != null)
            _programs.Remove(row);
        UpdateProgramsEmptyState();

        ConfigManager.Save(_config);
        _onProgramsChanged();
    }

    private void OnAutoSwitchToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        _config.AutoSwitchForPrograms = AutoSwitchCheck.IsChecked == true;
        ConfigManager.Save(_config);
        _onProgramsChanged();
    }

    private void UpdateProgramsEmptyState()
        => NoProgramsText.Visibility = _programs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void OnProfilesToggled(object sender, RoutedEventArgs e)
        => ProfilesPanel.Visibility = Collapsed(ProfilesToggle);

    private void OnSystemToggled(object sender, RoutedEventArgs e)
        => SystemPanel.Visibility = Collapsed(SystemToggle);

    private void OnTimingToggled(object sender, RoutedEventArgs e)
        => TimingPanel.Visibility = Collapsed(TimingToggle);

    private static Visibility Collapsed(System.Windows.Controls.Primitives.ToggleButton header)
        => header.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
}
