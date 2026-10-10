using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using TandemHdr.Configuration;
using TandemHdr.Native;
using TandemHdr.Services;
using Microsoft.Win32;
// WinForms and WPF are both enabled in this project and share several type names.
using Brush = System.Windows.Media.Brush;
using ComboBox = System.Windows.Controls.ComboBox;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using MessageBox = System.Windows.MessageBox;
using OpenFileDialog = Microsoft.Win32.OpenFileDialog;

namespace TandemHdr.Settings;

internal partial class SettingsWindow : Window
{
    private readonly AppConfig _config;
    private readonly IccProfileService _iccService;
    private readonly Func<HdrState> _getCurrentState;
    private readonly Func<string?> _getActiveProfileName;
    private readonly Action _onProfileChanged;
    private readonly Action _onRestartForUpdate;
    private readonly Action _onDataRemoved;
    private readonly DispatcherTimer _statusTimer;

    private bool _initializing = true;

    /// <summary>Set while the profile dropdowns are being filled, so populating them does
    /// not read back as the user choosing something.</summary>
    private bool _populatingProfiles;

    /// <summary>What the Updates section is currently showing, and what its button acts on.</summary>
    private UpdateCheck _update = UpdateService.Last;

    /// <summary>Whether the last failure came from a download rather than a check, so the
    /// status line can say which one went wrong.</summary>
    private bool _downloadFailed;

    /// <summary>One entry in a profile dropdown. Display is the file name, which is what the
    /// profile is applied by; Path is null only for the "not set" entry.</summary>
    internal sealed record ProfileOption(string Display, string? Path)
    {
        public override string ToString() => Display;
    }

    private static readonly ProfileOption NotSet = new("Not set", null);

    /// <summary>Last entry in each profile dropdown. Opens the file dialog for a profile
    /// that Windows has not installed yet.</summary>
    private static readonly ProfileOption BrowseOption = new("Browse for a file…", null);

    public SettingsWindow(
        AppConfig config,
        IccProfileService iccService,
        Func<HdrState> getCurrentState,
        Func<string?> getActiveProfileName,
        Action onProfileChanged,
        Action onProgramsChanged,
        Action onRestartForUpdate,
        Action onDataRemoved,
        bool readOnly = false)
    {
        InitializeComponent();

        _config = config;
        _iccService = iccService;
        _getCurrentState = getCurrentState;
        _getActiveProfileName = getActiveProfileName;
        _onProfileChanged = onProfileChanged;
        _onRestartForUpdate = onRestartForUpdate;
        _onDataRemoved = onDataRemoved;

        ProgramsPanel.Initialize(config, onProgramsChanged, readOnly);

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

    private void OnTabsLoaded(object sender, RoutedEventArgs e)
    {
        // The header strip has not been arranged yet when Loaded fires, so place the bar
        // once the layout pass has run — and without animating it in from zero width.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => MoveTabIndicator(animate: false)));
    }

    private void OnTabsSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // Selection changes inside the tab content (e.g. the programs list) bubble up here too.
        if (!ReferenceEquals(e.OriginalSource, Tabs)) return;

        MoveTabIndicator(animate: true);
    }

    /// <summary>Slides the shared underline in the TabControl template onto the selected tab.
    /// The insets mirror the tab item's own padding so the bar sits under the label, not the
    /// whole hit area.</summary>
    private void MoveTabIndicator(bool animate)
    {
        const double leadingInset = 14;   // tab padding
        const double trailingInset = 20;  // tab padding plus the 6px gap to the next tab

        if (Tabs.Template.FindName("SelectionIndicator", Tabs) is not Border indicator ||
            Tabs.Template.FindName("TabStrip", Tabs) is not FrameworkElement strip ||
            Tabs.SelectedItem is not TabItem item || item.ActualWidth <= 0)
            return;

        // Built here rather than in the template: freezables declared inside a ControlTemplate
        // come back frozen, and a frozen transform cannot be animated.
        if (indicator.RenderTransform is not TranslateTransform slide)
        {
            slide = new TranslateTransform();
            indicator.RenderTransform = slide;
        }

        double x = item.TranslatePoint(new System.Windows.Point(0, 0), strip).X + leadingInset;
        double width = Math.Max(item.ActualWidth - leadingInset - trailingInset, 0);

        if (!animate)
        {
            // Clear first: a held animation would otherwise keep overriding the local value.
            slide.BeginAnimation(TranslateTransform.XProperty, null);
            indicator.BeginAnimation(WidthProperty, null);
            slide.X = x;
            indicator.Width = width;
            return;
        }

        var glide = new Duration(TimeSpan.FromMilliseconds(220));
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };
        slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, glide) { EasingFunction = ease });
        indicator.BeginAnimation(WidthProperty, new DoubleAnimation(width, glide) { EasingFunction = ease });
    }

    private void LoadFromConfig()
    {
        PopulateProfileCombos();
        UpdateProfileWarning(SdrWarningText, _config.SdrProfilePath);
        UpdateProfileWarning(HdrWarningText, _config.HdrProfilePath);
        UpdateSetupBanner();

        AutoStartCheck.IsChecked = AutoStartService.IsEnabled();
        SyncExternalCheck.IsChecked = _config.SyncProfilesWithExternalHdrChanges;
        NotificationsCheck.IsChecked = _config.ShowNotifications;
        UpdateOnStartCheck.IsChecked = _config.CheckForUpdatesOnStart;

        // A staged exe from an earlier session outlives the check that produced it.
        if (UpdateService.IsUpdateStaged())
            _update = _update with { Status = UpdateStatus.Ready };
        RenderUpdateState();

    }

    /// <summary>Fills both dropdowns from the profiles Windows already has installed, which
    /// is where a calibrated profile normally ends up — so the common case needs no file
    /// browsing at all. The folder also holds printer and generic profiles; there is no
    /// reliable way to tell display profiles apart, so all of them are listed.</summary>
    private void PopulateProfileCombos()
    {
        var installed = IccProfileService.ListInstalledProfiles();

        _populatingProfiles = true;
        Fill(SdrProfileCombo, _config.SdrProfilePath);
        Fill(HdrProfileCombo, _config.HdrProfilePath);
        _populatingProfiles = false;

        void Fill(ComboBox combo, string? configured)
        {
            var options = new List<ProfileOption> { NotSet };
            options.AddRange(installed.Select(n =>
                new ProfileOption(n, Path.Combine(IccProfileService.SystemColorDir, n))));

            // A profile picked but never yet applied is not in the system directory, so it
            // would otherwise be missing from its own dropdown.
            string? name = string.IsNullOrWhiteSpace(configured) ? null : Path.GetFileName(configured);
            if (name != null && !options.Any(o => Same(o.Display, name)))
                options.Insert(1, new ProfileOption(name, configured));

            options.Add(BrowseOption);
            combo.ItemsSource = options;
            combo.SelectedItem = options.FirstOrDefault(o => name != null && Same(o.Display, name)) ?? NotSet;
        }
    }

    private static bool Same(string? a, string? b)
        => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>The app looks broken with no profiles set — it toggles HDR and nothing
    /// visibly follows — so say so until both are chosen.</summary>
    private void UpdateSetupBanner()
    {
        bool sdr = !string.IsNullOrWhiteSpace(_config.SdrProfilePath);
        bool hdr = !string.IsNullOrWhiteSpace(_config.HdrProfilePath);

        SetupBanner.Visibility = sdr && hdr ? Visibility.Collapsed : Visibility.Visible;
        SetupBannerText.Text = (sdr, hdr) switch
        {
            (false, false) => "Choose an SDR and an HDR profile below. Until then, Tandem HDR "
                            + "switches HDR but does not change your colour profile.",
            (true, false) => "Choose an HDR profile below. Until then, your SDR profile stays "
                           + "applied when HDR turns on.",
            _ => "Choose an SDR profile below. Until then, your HDR profile stays "
               + "applied when HDR turns off.",
        };
    }

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
        // Same wording as the tray menu's caption.
        StatusDetailText.Text = active != null ? $"Profile: {active}" : "No profile applied";
    }

    private void OnSdrProfileSelected(object sender, SelectionChangedEventArgs e)
        => ProfileSelected(isSdr: true);

    private void OnHdrProfileSelected(object sender, SelectionChangedEventArgs e)
        => ProfileSelected(isSdr: false);

    private void ProfileSelected(bool isSdr)
    {
        if (_initializing || _populatingProfiles) return;

        var combo = isSdr ? SdrProfileCombo : HdrProfileCombo;
        if (combo.SelectedItem is not ProfileOption option) return;

        // The browse entry is a command, not a value: put the selection back, then open the
        // dialog once this selection change has finished.
        if (ReferenceEquals(option, BrowseOption))
        {
            Dispatcher.BeginInvoke(() =>
            {
                PopulateProfileCombos();
                BrowseForProfile(isSdr);
            });
            return;
        }

        // Re-picking the profile already configured, just by its installed copy rather than
        // the file it was chosen from, is not a change: rewriting the path would only lose
        // track of the original.
        string? current = isSdr ? _config.SdrProfilePath : _config.HdrProfilePath;
        if (option.Path != null && current != null && Same(Path.GetFileName(current), option.Display))
            return;

        SetProfile(isSdr, option.Path);
    }

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

        SetProfile(isSdr, dialog.FileName);

        // Applying it installs it into the system directory, so rebuild the lists after.
        PopulateProfileCombos();
    }

    /// <summary>The one place a profile path changes: saves it, re-applies it and brings the
    /// warning, banner and status readout along.</summary>
    private void SetProfile(bool isSdr, string? path)
    {
        if (isSdr)
            _config.SdrProfilePath = path;
        else
            _config.HdrProfilePath = path;

        UpdateProfileWarning(isSdr ? SdrWarningText : HdrWarningText, path);
        ConfigManager.Save(_config);
        UpdateSetupBanner();
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

    private void OnUpdateOnStartToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        _config.CheckForUpdatesOnStart = UpdateOnStartCheck.IsChecked == true;
        ConfigManager.Save(_config);
    }

    /// <summary>One button for the whole flow: check, then download, then restart. What it
    /// does next is whatever the section is currently showing.</summary>
    private async void OnUpdateActionClick(object sender, RoutedEventArgs e)
    {
        if (_update.Status == UpdateStatus.Ready)
        {
            _onRestartForUpdate();
            return;
        }

        bool downloading = _update.Status == UpdateStatus.Available;
        UpdateActionButton.IsEnabled = false;
        UpdateStatusText.Text = downloading ? $"Downloading version {_update.LatestVersion}…" : "Checking for updates…";
        UpdateDetailText.Visibility = Visibility.Collapsed;

        _update = downloading ? await UpdateService.DownloadAsync(_update) : await UpdateService.CheckAsync();
        _downloadFailed = downloading && _update.Status == UpdateStatus.Failed;

        UpdateActionButton.IsEnabled = true;
        RenderUpdateState();
    }

    /// <summary>Marks the offered version as one not to be reminded about. It stays visible
    /// and installable here — skipping only silences the balloon at start-up.</summary>
    private void OnSkipVersionClick(object sender, RoutedEventArgs e)
    {
        _config.SkippedVersion = _update.LatestVersion?.ToString();
        ConfigManager.Save(_config);
        RenderUpdateState();
    }

    private void RenderUpdateState()
    {
        var current = UpdateService.CurrentVersion;
        bool available = _update.Status == UpdateStatus.Available;
        bool skipped = available && _update.LatestVersion?.ToString() == _config.SkippedVersion;

        var (status, detail, action) = _update.Status switch
        {
            UpdateStatus.UpToDate => ($"Up to date (v{current})", null, "Check now"),
            UpdateStatus.Available => ($"Version {_update.LatestVersion} is available",
                                       skipped ? $"You have v{current}. You chose to skip this version."
                                               : $"You have v{current}.", "Download and install"),
            UpdateStatus.Ready => ("Update ready to install",
                                   "Tandem HDR will restart to finish updating.", "Restart now"),
            UpdateStatus.Failed => (_downloadFailed ? "Could not download the update" : "Could not check for updates",
                                    _update.Error, "Try again"),
            _ => ($"Version {current}", null, "Check now"),
        };

        UpdateStatusText.Text = status;
        UpdateDetailText.Text = detail ?? string.Empty;
        UpdateDetailText.Visibility = detail == null ? Visibility.Collapsed : Visibility.Visible;
        UpdateActionButton.Content = action;

        SkipVersionButton.Visibility = available && !skipped ? Visibility.Visible : Visibility.Collapsed;

        // Kept up through Ready: what changed is still worth reading between downloading the
        // update and restarting for it.
        bool showNotes = (available || _update.Status == UpdateStatus.Ready)
                         && !string.IsNullOrWhiteSpace(_update.Notes);
        UpdateNotesText.Text = _update.Notes ?? string.Empty;
        UpdateNotesPanel.Visibility = showNotes ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Takes back everything the app has put outside its own folder, then hands over
    /// to the tray to close: leaving it running would let the next settings change write the
    /// registry key straight back.</summary>
    private void OnRemoveDataClick(object sender, RoutedEventArgs e)
    {
        var confirm = MessageBox.Show(this,
            "This removes Tandem HDR's settings, its start-up entry and its Start Menu "
            + "shortcut, then closes the app.\n\n"
            + "The program itself is not deleted — delete TandemHDR.exe yourself afterwards.",
            "Remove Tandem HDR", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (confirm != MessageBoxResult.OK) return;

        // Asked separately: the colour directory is shared with Windows and every other
        // colour-managed app, so a profile there may well still be wanted.
        var installed = new[] { _config.SdrProfilePath, _config.HdrProfilePath }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.GetFileName(p)!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(n => File.Exists(Path.Combine(IccProfileService.SystemColorDir, n)))
            .ToList();

        if (installed.Count > 0)
        {
            var alsoProfiles = MessageBox.Show(this,
                "Also remove the colour profiles Tandem HDR installed into Windows?\n\n"
                + string.Join("\n", installed)
                + "\n\nChoose No if you still want them available in Windows' colour settings.",
                "Remove colour profiles", MessageBoxButton.YesNo, MessageBoxImage.Question);

            if (alsoProfiles == MessageBoxResult.Yes)
            {
                foreach (string name in installed)
                {
                    if (!_iccService.UninstallProfile(name))
                        Logger.Log($"Could not remove '{name}' from the colour directory");
                }
            }
        }

        AutoStartService.SetEnabled(false);
        ShortcutHelper.RemoveShortcut();
        ConfigManager.Delete();

        Close();
        _onDataRemoved();
    }

    /// <summary>Escape closes the window. Not handled when a dropdown has it first — the
    /// ComboBox marks the key handled to close its popup, which stops this from running.</summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    private void OnNotificationsToggled(object sender, RoutedEventArgs e)
    {
        if (_initializing) return;

        _config.ShowNotifications = NotificationsCheck.IsChecked == true;
        ConfigManager.Save(_config);
    }

    private void OnMinimiseClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void OnMaximiseClick(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
