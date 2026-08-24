using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace TandemHdr.Services;

internal sealed record InstalledGame(string Name, string ExecutablePath, string Source, DateTime? LastPlayed);

/// <summary>
/// Finds installed games so the user can pick from a list instead of hunting for an .exe in
/// Program Files. Reads the launchers' own on-disk records — Steam's app manifests and Epic's
/// manifest files — rather than scanning the filesystem for anything that looks like a game.
///
/// Steam records a last-played timestamp, which is what lets the picker lead with the games
/// actually being played rather than an alphabetical wall.
/// </summary>
internal static class GameScanner
{
    /// <summary>Scans on a background thread: this touches the registry and walks game install
    /// directories, which is far too slow to run on the UI thread.</summary>
    public static Task<IReadOnlyList<InstalledGame>> ScanAsync() => Task.Run(Scan);

    private static IReadOnlyList<InstalledGame> Scan()
    {
        var found = new List<InstalledGame>();

        try { found.AddRange(ScanSteam()); }
        catch (Exception ex) { Logger.Log($"Steam scan failed: {ex.Message}"); }

        try { found.AddRange(ScanEpic()); }
        catch (Exception ex) { Logger.Log($"Epic scan failed: {ex.Message}"); }

        try { found.AddRange(ScanUbisoft()); }
        catch (Exception ex) { Logger.Log($"Ubisoft scan failed: {ex.Message}"); }

        try { found.AddRange(ScanEa()); }
        catch (Exception ex) { Logger.Log($"EA scan failed: {ex.Message}"); }

        try { found.AddRange(ScanXbox()); }
        catch (Exception ex) { Logger.Log($"Xbox scan failed: {ex.Message}"); }

        // Same executable found twice: through two launchers, or through library roots spelled
        // differently by the registry and libraryfolders.vdf ("c:/program files (x86)/steam"
        // against "C:\Program Files (x86)\Steam"). Group on the canonical form, not the text.
        var deduped = found
            .GroupBy(g => CanonicalPath(g.ExecutablePath), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            // Recently played first — that is almost always what the user is reaching for —
            // then everything never launched, alphabetically.
            .OrderByDescending(g => g.LastPlayed ?? DateTime.MinValue)
            .ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        Logger.Log($"Game scan found {deduped.Count} installed game(s)");
        return deduped;
    }

    #region Steam

    private static IEnumerable<InstalledGame> ScanSteam()
    {
        string? steamRoot = ReadSteamRoot();
        if (steamRoot == null || !Directory.Exists(steamRoot))
            return [];

        var games = new List<InstalledGame>();

        foreach (string library in SteamLibraries(steamRoot))
        {
            string appsDir = Path.Combine(library, "steamapps");
            if (!Directory.Exists(appsDir)) continue;

            foreach (string manifest in Directory.EnumerateFiles(appsDir, "appmanifest_*.acf"))
            {
                try
                {
                    string text = File.ReadAllText(manifest);
                    string? name = ReadVdfValue(text, "name");
                    string? installDir = ReadVdfValue(text, "installdir");
                    if (name == null || installDir == null) continue;
                    if (IsSteamTool(name)) continue;

                    string gameDir = Path.Combine(appsDir, "common", installDir);
                    string? exe = FindGameExecutable(gameDir, installDir);
                    if (exe == null) continue;

                    DateTime? lastPlayed = null;
                    if (long.TryParse(ReadVdfValue(text, "LastPlayed"), out long unix) && unix > 0)
                        lastPlayed = DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime;

                    games.Add(new InstalledGame(name, exe, "Steam", lastPlayed));
                }
                catch (Exception ex)
                {
                    Logger.Log($"Could not read {Path.GetFileName(manifest)}: {ex.Message}");
                }
            }
        }

        return games;
    }

    private static string? ReadSteamRoot()
    {
        // HKCU first: it is where Steam records the path for the logged-in user, and it is
        // readable without elevation.
        string?[] candidates =
        [
            Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath", null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Valve\Steam", "InstallPath", null) as string,
        ];

        return candidates.FirstOrDefault(p => !string.IsNullOrWhiteSpace(p));
    }

    /// <summary>Steam spreads games across libraries on different drives; libraryfolders.vdf
    /// lists them. The install root is always one of them, listed or not.</summary>
    private static IEnumerable<string> SteamLibraries(string steamRoot)
    {
        var libraries = new List<string> { steamRoot };

        string vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            foreach (Match match in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"([^\"]+)\""))
                libraries.Add(Unescape(match.Groups[1].Value));
        }

        return libraries
            .Select(CanonicalPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Collapses the spelling differences between the path Steam writes to the
    /// registry and the one it writes to libraryfolders.vdf.</summary>
    private static string CanonicalPath(string path)
    {
        try { return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar); }
        catch { return path; }
    }

    /// <summary>Pulls one top-level key out of a VDF/ACF file. A full VDF parser would be
    /// overkill for the three flat keys needed here.</summary>
    private static string? ReadVdfValue(string text, string key)
    {
        var match = Regex.Match(text, $"\"{Regex.Escape(key)}\"\\s+\"([^\"]*)\"", RegexOptions.IgnoreCase);
        return match.Success ? Unescape(match.Groups[1].Value) : null;
    }

    private static string Unescape(string value) => value.Replace(@"\\", @"\");

    /// <summary>Steam ships runtimes and tools through the same manifest mechanism as games;
    /// none of them belong in a list of things to turn HDR on for.</summary>
    private static bool IsSteamTool(string name)
        => SteamTools.Any(tool => name.Contains(tool, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] SteamTools =
    [
        "steamvr", "steamworks common redistributables", "proton", "steam linux runtime",
        "steam controller", "vrmonitor",
    ];

    #endregion

    #region Epic

    private static IEnumerable<InstalledGame> ScanEpic()
    {
        string manifestDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "Epic", "EpicGamesLauncher", "Data", "Manifests");

        if (!Directory.Exists(manifestDir))
            return [];

        var games = new List<InstalledGame>();

        foreach (string file in Directory.EnumerateFiles(manifestDir, "*.item"))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var root = doc.RootElement;

                if (root.TryGetProperty("bIsIncompleteInstall", out var incomplete) &&
                    incomplete.ValueKind == JsonValueKind.True)
                    continue;

                string? name = GetString(root, "DisplayName");
                string? location = GetString(root, "InstallLocation");
                if (name == null || location == null || !Directory.Exists(location))
                    continue;

                string? launch = GetString(root, "LaunchExecutable");
                string? exe = null;

                if (launch != null && !IsLauncherStub(launch))
                {
                    string candidate = Path.Combine(location, launch);
                    if (File.Exists(candidate))
                        exe = candidate;
                }

                // Either there was no usable LaunchExecutable, or it is a stub that hands off
                // to another launcher and exits — matching on it would never see the game
                // running. Fall back to picking the real executable out of the install folder.
                exe ??= FindGameExecutable(location, name);
                if (exe == null) continue;

                games.Add(new InstalledGame(name, exe, "Epic", null));
            }
            catch (Exception ex)
            {
                Logger.Log($"Could not read Epic manifest {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        return games;
    }

    private static string? GetString(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Thin bootstrappers that start the real game through another client and then
    /// exit. Watching one of these would see it vanish seconds after launch.</summary>
    private static bool IsLauncherStub(string executable)
    {
        string name = Path.GetFileNameWithoutExtension(executable);
        return LauncherStubs.Any(stub => name.Contains(stub, StringComparison.OrdinalIgnoreCase));
    }

    private static readonly string[] LauncherStubs =
        ["uplaylaunch", "ubisoftgamelauncher", "ealaunch", "eadesktop", "originlaunch", "bootstrapper"];

    #endregion

    #region Ubisoft

    /// <summary>Ubisoft Connect records one key per installed game holding only an install
    /// directory — no title — so the folder name is the best name available.</summary>
    private static IEnumerable<InstalledGame> ScanUbisoft()
    {
        const string root = @"SOFTWARE\WOW6432Node\Ubisoft\Launcher\Installs";
        using var key = Registry.LocalMachine.OpenSubKey(root);
        if (key == null) return [];

        var games = new List<InstalledGame>();

        foreach (string appId in key.GetSubKeyNames())
        {
            using var appKey = key.OpenSubKey(appId);
            // Written with forward slashes and a trailing separator; canonicalise before use.
            string? dir = appKey?.GetValue("InstallDir") as string;
            if (string.IsNullOrWhiteSpace(dir)) continue;

            dir = CanonicalPath(dir);
            if (!Directory.Exists(dir)) continue;

            string name = Path.GetFileName(dir);
            string? exe = FindGameExecutable(dir, name);
            if (exe != null)
                games.Add(new InstalledGame(name, exe, "Ubisoft", null));
        }

        return games;
    }

    #endregion

    #region EA

    /// <summary>
    /// EA is the least consistent of the four, and needs two passes.
    ///
    /// The registry (Origin Games) keeps a DisplayName per title but, on a modern EA App
    /// install, no InstallDir — and it keeps entries for games long since uninstalled, so it
    /// cannot be trusted on its own. The EA App instead records one folder per installed game
    /// under ProgramData, named after the game. That gives a reliable set of *names* with no
    /// paths, so each name is probed against the roots EA actually installs into (which
    /// includes bare Program Files, not just an EA Games folder).
    /// </summary>
    private static IEnumerable<InstalledGame> ScanEa()
    {
        var games = new List<InstalledGame>();

        using (var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Origin Games"))
        {
            foreach (string id in key?.GetSubKeyNames() ?? [])
            {
                using var gameKey = key!.OpenSubKey(id);
                string? dir = gameKey?.GetValue("InstallDir") as string;
                if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;

                string registryName = gameKey?.GetValue("DisplayName") as string
                                      ?? Path.GetFileName(CanonicalPath(dir));
                string? registryExe = FindGameExecutable(dir, registryName);
                if (registryExe != null)
                    games.Add(new InstalledGame(registryName, registryExe, "EA", null));
            }
        }

        foreach (string name in EaInstalledNames())
        {
            string? dir = EaInstallRoots()
                .Select(root => Path.Combine(root, name))
                .FirstOrDefault(Directory.Exists);
            if (dir == null) continue;

            string? exe = FindGameExecutable(dir, name);
            if (exe != null)
                games.Add(new InstalledGame(name, exe, "EA", null));
        }

        return games;
    }

    /// <summary>One folder per installed game, named after the game itself.</summary>
    private static IEnumerable<string> EaInstalledNames()
    {
        string installData = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "EA Desktop", "InstallData");

        if (!Directory.Exists(installData)) return [];

        try { return Directory.EnumerateDirectories(installData).Select(Path.GetFileName).OfType<string>(); }
        catch (Exception ex)
        {
            Logger.Log($"Could not list EA install data: {ex.Message}");
            return [];
        }
    }

    private static IEnumerable<string> EaInstallRoots()
    {
        string[] relative =
        [
            "Program Files", "Program Files (x86)",
            @"Program Files\EA Games", @"Program Files (x86)\EA Games", "EA Games",
            @"Program Files (x86)\Origin Games", "Origin Games",
            "",
        ];

        foreach (var drive in ReadyDrives())
            foreach (string tail in relative)
            {
                string path = tail.Length == 0 ? drive : Path.Combine(drive, tail);
                if (Directory.Exists(path)) yield return path;
            }
    }

    #endregion

    #region Xbox / Game Pass

    /// <summary>
    /// Game Pass installs sit one folder per game with the playable build under Content. Two
    /// root spellings are in the wild — "Program Files\Xbox Games" and a bare "XboxGames" at
    /// the drive root — and a machine can have both, on different drives.
    ///
    /// The packaged copies under WindowsApps are deliberately not touched: that tree is
    /// ACL-locked and unreadable without elevation.
    /// </summary>
    private static IEnumerable<InstalledGame> ScanXbox()
    {
        var games = new List<InstalledGame>();

        foreach (var drive in ReadyDrives())
        {
            foreach (string tail in (string[])[@"Program Files\Xbox Games", "XboxGames", "Xbox Games"])
            {
                string root = Path.Combine(drive, tail);
                if (!Directory.Exists(root)) continue;

                foreach (string dir in Directory.EnumerateDirectories(root))
                {
                    string name = Path.GetFileName(dir);

                    // The build that runs lives under Content; the sibling folders are package
                    // metadata and streaming blobs.
                    string content = Path.Combine(dir, "Content");
                    string searchIn = Directory.Exists(content) ? content : dir;

                    string? exe = FindGameExecutable(searchIn, name);
                    if (exe != null)
                        games.Add(new InstalledGame(name, exe, "Xbox", null));
                }
            }
        }

        return games;
    }

    #endregion

    /// <summary>Fixed drives only, and only ones currently ready: probing a disconnected or
    /// optical drive throws or stalls.</summary>
    private static IEnumerable<string> ReadyDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { return []; }

        return drives
            .Where(d =>
            {
                try { return d.IsReady && d.DriveType == DriveType.Fixed; }
                catch { return false; }
            })
            .Select(d => d.RootDirectory.FullName);
    }

    #region Executable picking

    // Support and anti-cheat executables that live alongside games. Several of these really do
    // run while playing, which is exactly why they must not be chosen as the game itself.
    private static readonly string[] ExcludedNameParts =
    [
        "unitycrashhandler", "unrealcefsubprocess", "crashpad", "crashreport", "crashhandler",
        "vcredist", "vc_redist", "dxsetup", "directx", "dotnetfx", "oalinst", "uninstall",
        "unins000", "anticheat", "battleye", "installer", "touchup", "dxwebsetup",
        // Bootstrappers that launch the real game and hand off. Matching on one of these
        // would stop seeing the game seconds after it starts. gamelaunchhelper.exe ships
        // beside every Game Pass title, so without it every Xbox game resolves to the stub.
        "start_protected_game", "launchpad", "gamelaunchhelper",
        // Demo build sitting next to the full one; never the thing being played.
        "_trial",
    ];

    private static readonly string[] ExcludedDirectoryParts =
    [
        "_commonredist", "redist", "directx", "dotnet", "easyanticheat", "battleye", "prereq",
    ];

    /// <summary>
    /// Picks the executable most likely to be the game itself. Prefers a name resembling the
    /// install folder (which is how most games are laid out), then shallower paths, then the
    /// largest file — a game binary dwarfs its helper tools.
    /// </summary>
    private static string? FindGameExecutable(string gameDir, string hint)
    {
        if (!Directory.Exists(gameDir)) return null;

        string normalisedHint = Normalise(hint);
        string? best = null;
        long bestScore = long.MinValue;
        int examined = 0;

        try
        {
            foreach (string exe in Directory.EnumerateFiles(gameDir, "*.exe", SearchOption.AllDirectories))
            {
                // Guard against pathological trees; the game binary is never 5,000 files deep.
                if (++examined > 5000) break;

                string name = Path.GetFileNameWithoutExtension(exe);
                if (ExcludedNameParts.Any(p => name.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    continue;

                string relative = exe[gameDir.Length..];
                if (ExcludedDirectoryParts.Any(p => relative.Contains(p, StringComparison.OrdinalIgnoreCase)))
                    continue;

                long size;
                try { size = new FileInfo(exe).Length; }
                catch { continue; }

                string normalisedName = Normalise(name);
                int depth = relative.Count(c => c == Path.DirectorySeparatorChar);

                long score = size / (1024 * 1024);          // megabytes, as the tiebreaker
                if (normalisedName == normalisedHint) score += 10_000;
                else if (normalisedHint.Contains(normalisedName) || normalisedName.Contains(normalisedHint))
                    score += 4_000;

                // Unreal Engine convention: the process that actually runs the game is
                // <Game>-Win64-Shipping.exe, buried under Binaries/, while the exe sitting in
                // the install root is usually a launcher that exits once the game starts.
                if (normalisedName.EndsWith("shipping", StringComparison.Ordinal)) score += 6_000;

                score -= depth * 200;

                if (score > bestScore)
                {
                    bestScore = score;
                    best = exe;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Could not search {gameDir}: {ex.Message}");
        }

        return best;
    }

    private static string Normalise(string value)
        => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    #endregion
}
