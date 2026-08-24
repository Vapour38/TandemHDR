using System.IO;

namespace TandemHdr.Services;

internal static class Logger
{
    private static readonly object Gate = new();
    private const long MaxBytes = 1024 * 1024;

    private static string LogPath
    {
        get
        {
            string dir = Path.GetDirectoryName(Environment.ProcessPath ?? Application.ExecutablePath)
                         ?? Environment.CurrentDirectory;
            return Path.Combine(dir, "tandemhdr.log");
        }
    }

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                string path = LogPath;
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                    File.Delete(path);

                File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take down the app.
        }
    }
}
