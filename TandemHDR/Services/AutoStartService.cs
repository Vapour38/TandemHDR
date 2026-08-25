using Microsoft.Win32;

namespace TandemHdr.Services;

/// <summary>
/// Manages the "start with Windows" registration via the per-user Run key. Shared by
/// the tray startup path and the settings window so there is one source of truth.
/// </summary>
internal static class AutoStartService
{
    private const string AppName = "Tandem HDR";

    /// <summary>Marks a launch as coming from the Windows Run key, so the tray starts
    /// quietly instead of opening the settings window the way a manual launch does.</summary>
    public const string StartupArgument = "--startup";
    private const string RegistryRunKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

    public static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, false);
        return key?.GetValue(AppName) != null;
    }

    public static void SetEnabled(bool enable)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RegistryRunKey, true);
        if (key == null) return;

        if (enable)
        {
            string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
            string desired = $"\"{exePath}\" {StartupArgument}";
            // Always rewrite: the entry goes stale if the exe is ever moved.
            if (key.GetValue(AppName) as string != desired)
            {
                key.SetValue(AppName, desired);
                Logger.Log($"Auto-start enabled -> {desired}");
            }
        }
        else
        {
            key.DeleteValue(AppName, false);
            Logger.Log("Auto-start disabled");
        }
    }
}
