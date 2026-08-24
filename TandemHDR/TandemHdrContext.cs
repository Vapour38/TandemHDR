using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using TandemHdr.Configuration;
using TandemHdr.Native;
using TandemHdr.Services;
using TandemHdr.Settings;

namespace TandemHdr;

internal class TandemHdrContext : ApplicationContext
{
    private readonly NotifyIcon _trayIcon;
    private readonly NativeWindow _msgWindow;
    private readonly System.Windows.Forms.Timer _stateCheckTimer;
    private readonly System.Windows.Forms.Timer _profileRefreshTimer;
    private readonly GameWatcher _gameWatcher;

    private readonly DisplayService _displayService;
    private readonly HdrService _hdrService;
    private readonly IccProfileService _iccService;

    private AppConfig _config;
    private HdrState _currentState;
    private string? _lastNotifiedProfile;

    // What HDR was doing before a watched program forced it on, so exiting the program
    // puts the display back the way the user had it rather than always turning HDR off.
    private HdrState? _stateBeforeProgram;

    private const string AppName = "Tandem HDR";

    // Native menu item IDs
    private const int IDM_ENABLE_HDR = 1;
    private const int IDM_SETTINGS = 2;
    private const int IDM_QUIT = 3;

    #region Native menu and dark mode interop

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, nuint uIDNewItem, string? lpNewItem);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr hMenu, uint uFlags, int x, int y, IntPtr hwnd, IntPtr lptpm);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetMenuDefaultItem(IntPtr hMenu, uint uItem, uint fByPos);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_CHECKED = 0x0008;
    private const uint MF_UNCHECKED = 0x0000;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_LEFTALIGN = 0x0000;
    private const uint TPM_RIGHTALIGN = 0x0008;
    private const int SM_MENUDROPALIGNMENT = 40;
    private const int SM_CXSMICON = 49;

    // Dark mode support via undocumented uxtheme.dll ordinals (same as HDRTray)
    private enum PreferredAppMode { Default, AllowDark, ForceDark, ForceLight, Max }

    [DllImport("uxtheme.dll", EntryPoint = "#135")]
    private static extern int SetPreferredAppMode(PreferredAppMode appMode);

    [DllImport("uxtheme.dll", EntryPoint = "#136")]
    private static extern void FlushMenuThemes();

    #endregion

    public TandemHdrContext(AppConfig config, DisplayService displayService,
        HdrService hdrService, IccProfileService iccService, HdrState initialState)
    {
        _config = config;
        _displayService = displayService;
        _hdrService = hdrService;
        _iccService = iccService;
        _currentState = initialState;

        // Hidden window to own the native popup menu
        _msgWindow = new NativeWindow();
        _msgWindow.CreateHandle(new CreateParams());

        // Enable dark mode for native menus
        try
        {
            SetPreferredAppMode(PreferredAppMode.ForceDark);
            FlushMenuThemes();
        }
        catch (Exception ex)
        {
            Logger.Log($"Dark mode init failed (expected on older Windows): {ex.Message}");
        }

        _trayIcon = new NotifyIcon
        {
            Visible = true,
        };
        _trayIcon.MouseClick += OnTrayIconClick;
        _trayIcon.MouseUp += OnTrayIconMouseUp;

        UpdateTrayState();

        _stateCheckTimer = new System.Windows.Forms.Timer
        {
            Interval = _config.HdrStateCheckIntervalSeconds * 1000,
        };
        _stateCheckTimer.Tick += OnStateCheckTick;
        _stateCheckTimer.Start();

        _profileRefreshTimer = new System.Windows.Forms.Timer
        {
            Interval = Math.Max(_config.ProfileRefreshIntervalSeconds, 1) * 1000,
        };
        _profileRefreshTimer.Tick += OnProfileRefreshTick;
        if (_config.ProfileRefreshIntervalSeconds > 0)
            _profileRefreshTimer.Start();

        _gameWatcher = new GameWatcher();
        _gameWatcher.FirstStarted += OnWatchedProgramStarted;
        _gameWatcher.LastExited += OnWatchedProgramExited;
        ApplyWatchListFromConfig();

        if (_config.StartWithWindows)
            AutoStartService.SetEnabled(true);

        // Show notification for the initial profile application
        var startupProfile = _iccService.ApplyProfileForState(initialState, _config.SdrProfilePath, _config.HdrProfilePath);
        ShowProfileNotification(startupProfile, initialState);
    }

    private void OnTrayIconClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
            DoToggle();
    }

    private void OnTrayIconMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right)
            ShowNativeContextMenu();
    }

    private void ShowNativeContextMenu()
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;

        try
        {
            // "Enable HDR" — checked when HDR is on
            uint hdrFlags = MF_STRING | (_currentState == HdrState.On ? MF_CHECKED : MF_UNCHECKED);
            AppendMenuW(menu, hdrFlags, IDM_ENABLE_HDR, "Enable HDR");

            // Make "Enable HDR" the default (bold) item
            SetMenuDefaultItem(menu, IDM_ENABLE_HDR, 0);

            AppendMenuW(menu, MF_SEPARATOR, 0, null);

            // "Settings…" — opens the settings window
            AppendMenuW(menu, MF_STRING, IDM_SETTINGS, "Settings…");

            // "Quit"
            AppendMenuW(menu, MF_STRING, IDM_QUIT, "Quit");

            // Get cursor position for menu placement
            var cursorPos = Cursor.Position;

            // Required for the menu to dismiss when clicking outside
            SetForegroundWindow(_msgWindow.Handle);

            // Align based on system setting
            bool rightAlign = GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0;
            uint flags = TPM_RETURNCMD | TPM_RIGHTBUTTON | (rightAlign ? TPM_RIGHTALIGN : TPM_LEFTALIGN);

            int cmd = TrackPopupMenuEx(menu, flags, cursorPos.X, cursorPos.Y, _msgWindow.Handle, IntPtr.Zero);

            switch (cmd)
            {
                case IDM_ENABLE_HDR:
                    DoToggle();
                    break;
                case IDM_SETTINGS:
                    OpenSettings();
                    break;
                case IDM_QUIT:
                    _stateCheckTimer.Stop();
                    _profileRefreshTimer.Stop();
                    _gameWatcher.Stop();
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                    Application.Exit();
                    break;
            }
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void OpenSettings()
    {
        WpfHost.ShowSettings(_config, _displayService, _hdrService, _iccService,
            getCurrentState: () => _currentState,
            getActiveProfileName: () => _lastNotifiedProfile,
            onIntervalsChanged: OnSettingsIntervalsChanged,
            onProfileChanged: OnSettingsProfileChanged,
            onProgramsChanged: ApplyWatchListFromConfig);
    }

    /// <summary>Live-applies interval edits made in the settings window to the already-running timers.</summary>
    private void OnSettingsIntervalsChanged()
    {
        _stateCheckTimer.Interval = _config.HdrStateCheckIntervalSeconds * 1000;

        _profileRefreshTimer.Interval = Math.Max(_config.ProfileRefreshIntervalSeconds, 1) * 1000;
        if (_config.ProfileRefreshIntervalSeconds > 0)
            _profileRefreshTimer.Start();
        else
            _profileRefreshTimer.Stop();
    }

    /// <summary>Re-applies the profile for the current state after a profile path edit in the settings window.</summary>
    private void OnSettingsProfileChanged()
    {
        var appliedProfile = _iccService.ApplyProfileForState(_currentState, _config.SdrProfilePath, _config.HdrProfilePath);
        ShowProfileNotification(appliedProfile, _currentState);
    }

    /// <summary>Pushes the configured whitelist into the watcher and starts or stops it to
    /// match the master switch. Safe to call repeatedly — the settings window calls it on
    /// every edit.</summary>
    private void ApplyWatchListFromConfig()
    {
        if (!_config.AutoSwitchForPrograms)
        {
            _gameWatcher.Stop();
            _stateBeforeProgram = null;
            return;
        }

        _gameWatcher.SetWatchList(_config.HdrPrograms);
        _gameWatcher.Start();
    }

    private void OnWatchedProgramStarted(string program)
    {
        if (_currentState == HdrState.Unsupported) return;

        _stateBeforeProgram = _currentState;
        Logger.Log($"{program} started; forcing HDR on (was {_currentState})");
        ApplyHdrState(enable: true);
    }

    private void OnWatchedProgramExited(string program)
    {
        if (_currentState == HdrState.Unsupported) return;

        // Restore, don't blindly switch off: if HDR was already on before the program
        // started, the user gets to keep it on.
        bool restoreOn = _stateBeforeProgram == HdrState.On;
        _stateBeforeProgram = null;
        Logger.Log($"{program} exited; restoring HDR to {(restoreOn ? "ON" : "OFF")}");
        ApplyHdrState(enable: restoreOn);
    }

    /// <summary>Drives HDR to a known state and brings the tray, colour profile and
    /// notification along with it. Shared by the watcher and the manual toggle.</summary>
    private void ApplyHdrState(bool enable)
    {
        try
        {
            _currentState = _hdrService.SetHdr(enable);
            UpdateTrayState();
            var appliedProfile = _iccService.ApplyProfileForState(_currentState, _config.SdrProfilePath, _config.HdrProfilePath);
            ShowProfileNotification(appliedProfile, _currentState);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to set HDR to {(enable ? "on" : "off")}: {ex}");
        }
    }

    private void DoToggle()
    {
        if (_currentState == HdrState.Unsupported)
        {
            _trayIcon.ShowBalloonTip(3000, AppName, "No HDR-capable display found.", ToolTipIcon.Warning);
            return;
        }

        try
        {
            var newState = _hdrService.ToggleHdr();
            _currentState = newState;
            UpdateTrayState();
            var appliedProfile = _iccService.ApplyProfileForState(newState, _config.SdrProfilePath, _config.HdrProfilePath);
            ShowProfileNotification(appliedProfile, newState);
        }
        catch (Exception ex)
        {
            Logger.Log($"Toggle failed: {ex}");
            _trayIcon.ShowBalloonTip(3000, AppName, "Failed to toggle HDR. Check log for details.", ToolTipIcon.Error);
        }
    }

    private void OnStateCheckTick(object? sender, EventArgs e)
    {
        try
        {
            var state = _hdrService.GetHdrState();
            if (state == _currentState) return;

            Logger.Log($"External HDR state change detected: {_currentState} -> {state}");
            _currentState = state;
            UpdateTrayState();

            if (_config.SyncProfilesWithExternalHdrChanges)
            {
                var appliedProfile = _iccService.ApplyProfileForState(state, _config.SdrProfilePath, _config.HdrProfilePath);
                ShowProfileNotification(appliedProfile, state);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"State check failed: {ex.Message}");
        }
    }

    private void OnProfileRefreshTick(object? sender, EventArgs e)
    {
        try
        {
            _iccService.ApplyProfileForState(_currentState, _config.SdrProfilePath, _config.HdrProfilePath);
        }
        catch (Exception ex)
        {
            Logger.Log($"Profile refresh failed: {ex.Message}");
        }
    }

    private void ShowProfileNotification(string? profileName, HdrState state)
    {
        if (profileName == null || profileName == _lastNotifiedProfile)
            return;

        _lastNotifiedProfile = profileName;
        string mode = state == HdrState.On ? "HDR" : "SDR";
        _trayIcon.ShowBalloonTip(3000, AppName, $"Switched to {mode}: Applied '{profileName}'", ToolTipIcon.Info);
    }

    private void UpdateTrayState()
    {
        _trayIcon.Text = _currentState switch
        {
            HdrState.On => "HDR is on\nClick to turn off HDR",
            HdrState.Off => "HDR is off\nClick to turn on HDR",
            _ => "HDR is unsupported",
        };

        _trayIcon.Icon = CreateIcon(_currentState);
    }

    private static Icon CreateIcon(HdrState state)
    {
        // Query the actual system tray icon size (DPI-aware: 16 @100%, 20 @125%, 24 @150%, 32 @200%)
        int iconSize = GetSystemMetrics(SM_CXSMICON);
        if (iconSize < 16) iconSize = 16;

        // Render at 4x for quality, Windows will display at native size
        int renderSize = iconSize * 4;

        var bmp = new Bitmap(renderSize, renderSize);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        string text = state == HdrState.On ? "HDR" : "SDR";

        // Use TextRenderer (GDI) for pixel-accurate text measurement and rendering
        float fontSize = renderSize * 0.48f;
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);

        var flags = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter
                  | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;
        var iconRect = new Rectangle(0, 0, renderSize, renderSize);

        // Draw dark outline for contrast on any taskbar color (like HDRTray's stroke)
        int outlineWidth = Math.Max(1, renderSize / 25);
        Color outlineColor = Color.FromArgb(200, 0, 0, 0);
        for (int ox = -outlineWidth; ox <= outlineWidth; ox++)
        {
            for (int oy = -outlineWidth; oy <= outlineWidth; oy++)
            {
                if (ox == 0 && oy == 0) continue;
                var offsetRect = new Rectangle(ox, oy, renderSize, renderSize);
                TextRenderer.DrawText(g, text, font, offsetRect, outlineColor, flags);
            }
        }

        // Draw main text
        Color textColor = state switch
        {
            HdrState.On => Color.White,
            HdrState.Off => Color.FromArgb(180, 180, 180),
            _ => Color.FromArgb(140, 60, 60),
        };
        TextRenderer.DrawText(g, text, font, iconRect, textColor, flags);

        // Scale down to actual icon size for a crisp result
        var icon_bmp = new Bitmap(iconSize, iconSize);
        using var ig = Graphics.FromImage(icon_bmp);
        ig.InterpolationMode = InterpolationMode.HighQualityBicubic;
        ig.SmoothingMode = SmoothingMode.AntiAlias;
        ig.DrawImage(bmp, 0, 0, iconSize, iconSize);
        bmp.Dispose();

        var handle = icon_bmp.GetHicon();
        return Icon.FromHandle(handle);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _stateCheckTimer.Dispose();
            _profileRefreshTimer.Dispose();
            _gameWatcher.Dispose();
            _trayIcon.Dispose();
            _msgWindow.DestroyHandle();
        }
        base.Dispose(disposing);
    }
}
