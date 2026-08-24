using System.Runtime.InteropServices;

namespace TandemHdr.Native;

/// <summary>
/// Thin wrapper over the DWM window-attribute and colorization APIs used to give the
/// settings window native Windows 11 chrome: dark title bar, Mica backdrop, rounded
/// corners, and the current system accent color.
/// </summary>
internal static class DwmApi
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(out uint colorizationColor, [MarshalAs(UnmanagedType.Bool)] out bool opaqueBlend);

    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMWCP_ROUND = 2;
    private const int DWMSBT_MAINWINDOW = 2;     // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic

    public static void SetRoundedCorners(IntPtr hwnd)
    {
        int value = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref value, sizeof(int));
    }

    /// <summary>Requests the Acrylic backdrop (Windows 11 22H2+) — the blurred, translucent
    /// surface that samples what is behind the window, rather than Mica's near-opaque
    /// desktop-wallpaper tint. Returns false on older systems so the caller can fall back.</summary>
    public static bool TrySetAcrylicBackdrop(IntPtr hwnd)
    {
        int value = DWMSBT_TRANSIENTWINDOW;
        return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int)) == 0;
    }

    /// <summary>Requests the Mica backdrop (Windows 11 22H2+). Returns false on older
    /// systems so the caller can fall back to a flat theme-colored background.</summary>
    public static bool TrySetMicaBackdrop(IntPtr hwnd)
    {
        int value = DWMSBT_MAINWINDOW;
        return DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref value, sizeof(int)) == 0;
    }

    /// <summary>Reads the current Windows accent/colorization color (ARGB). This is the
    /// same signal Explorer uses for title bars and taskbar glow, and needs no WinRT
    /// dependency.</summary>
    public static bool TryGetAccentColor(out uint argb)
    {
        int hr = DwmGetColorizationColor(out uint colorizationColor, out _);
        argb = colorizationColor;
        return hr == 0;
    }
}
