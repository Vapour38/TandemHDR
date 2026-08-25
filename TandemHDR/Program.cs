using TandemHdr.Configuration;
using TandemHdr.Services;

namespace TandemHdr;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => Logger.Log($"Unhandled thread exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.Log($"Unhandled exception: {e.ExceptionObject}");

        // Set App User Model ID so Windows toast notifications show "Tandem HDR" and our icon
        ShortcutHelper.SetCurrentProcessExplicitAppUserModelID(ShortcutHelper.AppUserModelId);
        ShortcutHelper.EnsureShortcut();

        // A relaunch after an update overlaps the process it replaced, so wait for that
        // one to let go of the mutex instead of treating it as a second instance.
        bool relaunchedAfterUpdate = args.Contains(UpdateService.UpdatedArgument, StringComparer.OrdinalIgnoreCase);

        using var mutex = new Mutex(false, @"Global\TandemHdr_SingleInstance");
        if (!TryAcquire(mutex, relaunchedAfterUpdate ? TimeSpan.FromSeconds(15) : TimeSpan.Zero))
        {
            Logger.Log("Another instance is already running, exiting");
            return;
        }

        Logger.Log("Tandem HDR starting");
        UpdateService.CleanUpSupersededExe();

        var config = ConfigManager.Load();
        var displayService = new DisplayService();
        var hdrService = new HdrService(displayService);
        var iccService = new IccProfileService(displayService);

        var initialState = hdrService.GetHdrState();
        Logger.Log($"Initial HDR state: {initialState}");

        // A manual launch has no visible window otherwise, so open settings; a launch from
        // the Run key at sign-in stays in the tray.
        bool launchedByWindows = args.Contains(AutoStartService.StartupArgument, StringComparer.OrdinalIgnoreCase);

        Application.Run(new TandemHdrContext(config, displayService, hdrService, iccService, initialState,
            openSettingsOnStart: !launchedByWindows));

        Logger.Log("Tandem HDR exiting");
    }

    /// <summary>Waits up to <paramref name="timeout"/> for the single-instance mutex. A
    /// process that exits without releasing it leaves it abandoned, which still means the
    /// slot is free.</summary>
    private static bool TryAcquire(Mutex mutex, TimeSpan timeout)
    {
        try
        {
            return mutex.WaitOne(timeout);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }
}
