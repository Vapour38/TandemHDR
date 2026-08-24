using TandemHdr.Native;
using Microsoft.Win32;
// System.Drawing (WinForms) and System.Windows.Media (WPF) both define Color.
using Color = System.Windows.Media.Color;

namespace TandemHdr.Settings;

/// <summary>Reads the current Windows theme (light/dark) and accent color so the
/// settings window can match the rest of the OS rather than carrying its own palette.</summary>
internal static class NativeTheme
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private static readonly Color FallbackAccent = Color.FromRgb(0x00, 0x67, 0xC0); // Windows default accent blue

    public static bool IsDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(PersonalizeKey, false);
            // AppsUseLightTheme: 0 = dark, 1 = light. Absent means an OS build too old to ask.
            var value = key?.GetValue("AppsUseLightTheme");
            return value is int i && i == 0;
        }
        catch
        {
            return false;
        }
    }

    public static Color GetAccentColor()
    {
        if (!DwmApi.TryGetAccentColor(out uint argb))
            return FallbackAccent;

        byte a = (byte)(argb >> 24), r = (byte)(argb >> 16), g = (byte)(argb >> 8), b = (byte)argb;
        if (a == 0 && r == 0 && g == 0 && b == 0)
            return FallbackAccent;

        return Color.FromRgb(r, g, b);
    }
}
