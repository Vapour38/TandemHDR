using TandemHdr.Configuration;
using TandemHdr.Services;

namespace TandemHdr;

static class Program
{
    [STAThread]
    static void Main()
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        Application.ThreadException += (_, e) => Logger.Log($"Unhandled thread exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Logger.Log($"Unhandled exception: {e.ExceptionObject}");

        // Set App User Model ID so Windows toast notifications show "Tandem HDR" and our icon
        ShortcutHelper.SetCurrentProcessExplicitAppUserModelID(ShortcutHelper.AppUserModelId);
        ShortcutHelper.EnsureShortcut();

        using var mutex = new Mutex(true, @"Global\TandemHdr_SingleInstance", out bool createdNew);
        if (!createdNew)
        {
            Logger.Log("Another instance is already running, exiting");
            return;
        }

        Logger.Log("Tandem HDR starting");

        var config = ConfigManager.Load();
        var displayService = new DisplayService();
        var hdrService = new HdrService(displayService);
        var iccService = new IccProfileService(displayService);

        var initialState = hdrService.GetHdrState();
        Logger.Log($"Initial HDR state: {initialState}");

        Application.Run(new TandemHdrContext(config, displayService, hdrService, iccService, initialState));

        Logger.Log("Tandem HDR exiting");
    }
}
