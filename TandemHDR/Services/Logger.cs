using System.Diagnostics;

namespace TandemHdr.Services;

/// <summary>
/// Diagnostics go to the debugger's output window only. Nothing is written to disk: the
/// app is a single exe that must not leave files beside itself.
/// </summary>
internal static class Logger
{
    public static void Log(string message) =>
        Debug.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
}
