using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace TandemHdr.Services;

internal sealed record RecentProgram(string Name, string ExecutablePath, DateTime? LastRun, int RunCount);

/// <summary>
/// Lists executables this user has actually launched, so a game no launcher knows about —
/// a standalone install, an emulator, something from GOG or itch — can still be picked from
/// a list instead of hunted for by hand.
///
/// Source is Explorer's UserAssist, which records per-user launch counts and timestamps. It
/// needs no elevation, unlike the alternatives: Prefetch and BAM are both admin-only.
/// </summary>
internal static class RecentPrograms
{
    // UserAssist splits its records across GUID-named subkeys. This one holds executables;
    // its sibling holds shortcut links, which would just duplicate them under .lnk names.
    private const string ExecutablesGuid = "{CEBFF5CD-ACE2-4F4F-9178-9926F41749EA}";

    private const string UserAssistRoot =
        @"Software\Microsoft\Windows\CurrentVersion\Explorer\UserAssist";

    public static Task<IReadOnlyList<RecentProgram>> ListAsync() => Task.Run(List);

    private static IReadOnlyList<RecentProgram> List()
    {
        var found = new Dictionary<string, RecentProgram>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey($@"{UserAssistRoot}\{ExecutablesGuid}\Count");
            if (key == null) return [];

            foreach (string encoded in key.GetValueNames())
            {
                if (key.GetValue(encoded) is not byte[] data) continue;

                string path = ResolveKnownFolder(Rot13(encoded));
                if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (IsNoise(path)) continue;
                if (!File.Exists(path)) continue;

                var (lastRun, runCount) = ReadCounters(data);

                // The same executable can appear under more than one spelling; keep whichever
                // record says it ran most recently.
                string name = Path.GetFileNameWithoutExtension(path);
                if (found.TryGetValue(path, out var existing) &&
                    (existing.LastRun ?? DateTime.MinValue) >= (lastRun ?? DateTime.MinValue))
                    continue;

                found[path] = new RecentProgram(name, path, lastRun, runCount);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Recent programs lookup failed: {ex.Message}");
            return [];
        }

        AddCompatibilityStoreEntries(found);

        var ordered = found.Values
            // Anything with a timestamp first, newest down; UserAssist only timestamps what
            // was launched through Explorer, so the rest are ordered by name rather than
            // pretending an unknown date is an old one.
            .OrderByDescending(p => p.LastRun.HasValue)
            .ThenByDescending(p => p.LastRun ?? DateTime.MinValue)
            .ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Logger.Log($"Recent programs: {ordered.Count} executable(s)");
        return ordered;
    }

    /// <summary>
    /// Second source, for coverage. UserAssist only records what was launched through
    /// Explorer, which misses anything started by a launcher — so on a machine where games run
    /// from Steam it can be both short and stale. The Compatibility Assistant store holds far
    /// more executables that have actually run, as plain full paths, but carries no
    /// timestamps; entries already known from UserAssist keep their date.
    /// </summary>
    private static void AddCompatibilityStoreEntries(Dictionary<string, RecentProgram> found)
    {
        const string store = @"Software\Microsoft\Windows NT\CurrentVersion\AppCompatFlags\Compatibility Assistant\Store";

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(store);
            if (key == null) return;

            foreach (string path in key.GetValueNames())
            {
                if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                if (found.ContainsKey(path)) continue;
                if (IsNoise(path) || !File.Exists(path)) continue;

                found[path] = new RecentProgram(Path.GetFileNameWithoutExtension(path), path, null, 0);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Compatibility store lookup failed: {ex.Message}");
        }
    }

    /// <summary>UserAssist obfuscates its value names with ROT13 — not encryption, just enough
    /// to keep them out of a plain registry search.</summary>
    private static string Rot13(string value)
    {
        var chars = value.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (c is >= 'a' and <= 'z') chars[i] = (char)('a' + (c - 'a' + 13) % 26);
            else if (c is >= 'A' and <= 'Z') chars[i] = (char)('A' + (c - 'A' + 13) % 26);
        }
        return new string(chars);
    }

    /// <summary>
    /// Records are stored relative to a known folder, written as a leading GUID —
    /// "{6D809377-...}\Steam\steam.exe". Resolving through SHGetKnownFolderPath rather than a
    /// hardcoded table keeps it correct on machines that relocated their Program Files.
    /// </summary>
    private static string ResolveKnownFolder(string entry)
    {
        if (entry.Length == 0 || entry[0] != '{') return entry;

        int close = entry.IndexOf('}');
        if (close < 0 || !Guid.TryParse(entry[..(close + 1)], out Guid folderId))
            return entry;

        string remainder = entry[(close + 1)..].TrimStart(Path.DirectorySeparatorChar);

        try
        {
            if (SHGetKnownFolderPath(folderId, 0, IntPtr.Zero, out string folder) == 0)
                return Path.Combine(folder, remainder);
        }
        catch (Exception ex)
        {
            Logger.Log($"Known folder {folderId} could not be resolved: {ex.Message}");
        }

        return entry;
    }

    /// <summary>Win7-and-later UserAssist record: a 72-byte blob with the run count at offset
    /// 4 and the last-executed FILETIME at offset 60.</summary>
    private static (DateTime? LastRun, int RunCount) ReadCounters(byte[] data)
    {
        if (data.Length < 68) return (null, 0);

        int runCount = BitConverter.ToInt32(data, 4);
        long filetime = BitConverter.ToInt64(data, 60);

        DateTime? lastRun = null;
        if (filetime > 0)
        {
            try { lastRun = DateTime.FromFileTime(filetime); }
            catch (ArgumentOutOfRangeException) { /* garbage timestamp; leave it unset */ }
        }

        // A wildly future timestamp means the blob was not the layout expected.
        if (lastRun > DateTime.Now.AddDays(1)) lastRun = null;

        return (lastRun, runCount < 0 ? 0 : runCount);
    }

    /// <summary>
    /// Keeps Windows' own plumbing out of the list. The point of this list is "things I ran
    /// that might be a game", and a list led by installers, updaters and control-panel applets
    /// would be useless.
    /// </summary>
    private static bool IsNoise(string path)
    {
        string windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (path.StartsWith(windows, StringComparison.OrdinalIgnoreCase)) return true;

        string name = Path.GetFileNameWithoutExtension(path);
        return NoiseNames.Any(n => name.Contains(n, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] NoiseNames =
    [
        "setup", "install", "uninstall", "unins", "update", "updater", "patch", "redist",
        "vcredist", "crashhandler", "crashreport", "crashpad", "helper", "webview",
        "tandemhdr",
    ];

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rfid, uint flags, IntPtr token,
        [MarshalAs(UnmanagedType.LPWStr)] out string path);
}
