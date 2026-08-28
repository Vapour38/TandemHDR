using System.Windows.Media;
using TandemHdr.Configuration;
using TandemHdr.Native;
using TandemHdr.Services;
// System.Windows.Forms and System.Windows (WPF) both define Application.
using Application = System.Windows.Application;
using ShutdownMode = System.Windows.ShutdownMode;
using ResourceDictionary = System.Windows.ResourceDictionary;

namespace TandemHdr.Settings;

/// <summary>
/// Bridges the WinForms tray shell to the WPF settings window. A WPF <see cref="Window"/>
/// shows fine from a WinForms message loop on the same STA thread (both dispatch through
/// the same Win32 GetMessage loop) as long as a <see cref="Application"/> instance exists
/// to own resources/dispatcher shutdown, which is what this class sets up once, lazily.
/// </summary>
internal static class WpfHost
{
    private static Application? _app;
    private static SettingsWindow? _window;

    public static void ShowSettings(
        AppConfig config,
        DisplayService displayService,
        HdrService hdrService,
        IccProfileService iccService,
        Func<HdrState> getCurrentState,
        Func<string?> getActiveProfileName,
        Action onProfileChanged,
        Action onProgramsChanged,
        Action onRestartForUpdate)
    {
        EnsureApplication();

        if (_window != null)
        {
            _window.Activate();
            return;
        }

        _window = new SettingsWindow(config, displayService, hdrService, iccService,
            getCurrentState, getActiveProfileName, onProfileChanged,
            onProgramsChanged, onRestartForUpdate);
        _window.Closed += (_, _) => _window = null;
        _window.Show();
        _window.Activate();
    }

    private static void EnsureApplication()
    {
        if (_app != null) return;

        // OnExplicitShutdown: closing the settings window must not tear down the
        // dispatcher/app object out from under the tray, which owns the process lifetime.
        _app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        bool dark = NativeTheme.IsDark();
        _app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri(dark
                ? "pack://application:,,,/TandemHDR;component/Theme/DarkBrushes.xaml"
                : "pack://application:,,,/TandemHDR;component/Theme/LightBrushes.xaml")
        });
        _app.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("pack://application:,,,/TandemHDR;component/Theme/Styles.xaml")
        });

        // The system accent color has no light/dark variant of its own, so it lives as a
        // plain top-level resource rather than in the swapped theme dictionaries.
        _app.Resources["AccentBrush"] = new SolidColorBrush(NativeTheme.GetAccentColor());

        Logger.Log("WPF host initialized for settings window");
    }
}
