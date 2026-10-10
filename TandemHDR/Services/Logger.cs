using System.Diagnostics;

namespace TandemHdr.Services;

/// <summary>
/// Diagnostics go to the debugger's output window only (DebugView shows them without a
/// debugger). Nothing is written to disk: the app is a single exe that must not leave files
/// beside itself. Trace, not Debug: Debug calls are compiled out of Release builds.
/// </summary>
internal static class Logger
{
    public static void Log(string message) =>
        Trace.WriteLine($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}");
}
