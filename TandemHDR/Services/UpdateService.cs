using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TandemHdr.Services;

internal enum UpdateStatus
{
    /// <summary>No check has run yet this session.</summary>
    Unknown,
    UpToDate,
    Available,
    /// <summary>Downloaded and verified; the swap happens on the next launch.</summary>
    Ready,
    Failed,
}

internal record UpdateCheck(UpdateStatus Status, Version? LatestVersion = null,
    string? DownloadUrl = null, string? Sha256 = null, string? Error = null);

/// <summary>
/// Self-update against the project's GitHub releases: there is no update server, the
/// release feed is the API and the release zip is the payload. The new exe is staged
/// beside the running one and swapped in by rename, which Windows allows even while the
/// old exe is running.
/// </summary>
internal static class UpdateService
{
    private const string LatestReleaseApi = "https://api.github.com/repos/Vapour38/TandemHDR/releases/latest";
    private const string ExeName = "TandemHDR.exe";
    private const string StagedName = "TandemHDR.update.exe";
    private const string SupersededName = "TandemHDR.old.exe";

    /// <summary>Marks the relaunch after a swap, so the new process waits for the old one
    /// to release the single-instance mutex instead of deciding it is a duplicate.</summary>
    public const string UpdatedArgument = "--updated";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>The most recent check, so the settings window can show what the startup
    /// check already found instead of asking GitHub again.</summary>
    public static UpdateCheck Last { get; private set; } = new(UpdateStatus.Unknown);

    public static Version CurrentVersion
    {
        get
        {
            var v = Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, v.Build);
        }
    }

    private static string ExeDir
        => Path.GetDirectoryName(Environment.ProcessPath ?? Application.ExecutablePath) ?? Environment.CurrentDirectory;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // GitHub rejects requests without one.
        client.DefaultRequestHeaders.Add("User-Agent", "TandemHDR");
        client.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        return client;
    }

    /// <summary>Deletes the exe the last update replaced. Called at startup, once the
    /// superseded file is no longer locked by the process that was running from it.</summary>
    public static void CleanUpSupersededExe()
    {
        try
        {
            string old = Path.Combine(ExeDir, SupersededName);
            if (File.Exists(old))
            {
                File.Delete(old);
                Logger.Log("Removed the superseded exe from the last update");
            }
        }
        catch (Exception ex)
        {
            // Still locked, or read-only. It gets retried on the next launch.
            Logger.Log($"Could not remove the superseded exe: {ex.Message}");
        }
    }

    /// <summary>True when an update has been downloaded and is waiting for a restart.</summary>
    public static bool IsUpdateStaged() => File.Exists(Path.Combine(ExeDir, StagedName));

    public static async Task<UpdateCheck> CheckAsync()
    {
        try
        {
            string json = await Http.GetStringAsync(LatestReleaseApi);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            if (!Version.TryParse(tag.TrimStart('v', 'V'), out var parsed))
                return Fail($"Unrecognised release tag ‘{tag}’");

            var latest = new Version(parsed.Major, parsed.Minor, Math.Max(parsed.Build, 0));
            if (latest <= CurrentVersion)
            {
                Logger.Log($"Up to date: latest release is v{latest}, running v{CurrentVersion}");
                return Record(new UpdateCheck(UpdateStatus.UpToDate, latest));
            }

            string? url = null;
            foreach (var asset in root.GetProperty("assets").EnumerateArray())
            {
                string name = asset.GetProperty("name").GetString() ?? string.Empty;
                if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    url = asset.GetProperty("browser_download_url").GetString();
                    break;
                }
            }

            if (url == null)
                return Fail($"Release v{latest} has no zip to download");

            // The release notes carry the zip's hash; without it there is nothing to check
            // the download against, so the update is not offered.
            string body = root.TryGetProperty("body", out var b) ? b.GetString() ?? string.Empty : string.Empty;
            // Lazy and generously bounded: the hash trails the "SHA-256" label after
            // whatever punctuation and file name the notes happen to put in between.
            var match = Regex.Match(body, "SHA-?256[\s\S]{0,120}?([0-9a-fA-F]{64})", RegexOptions.IgnoreCase);
            if (!match.Success)
                return Fail($"Release v{latest} publishes no SHA-256 to verify against");

            Logger.Log($"Update available: v{latest} (running v{CurrentVersion})");
            return Record(new UpdateCheck(UpdateStatus.Available, latest, url, match.Groups[1].Value.ToLowerInvariant()));
        }
        catch (Exception ex)
        {
            return Fail(ex.Message);
        }
    }

    /// <summary>Downloads the release zip, verifies it against the published hash and
    /// extracts the exe next to the running one. Nothing is replaced until the restart.</summary>
    public static async Task<UpdateCheck> DownloadAsync(UpdateCheck available)
    {
        if (available.DownloadUrl == null || available.Sha256 == null || available.LatestVersion == null)
            return Fail("Nothing to download");

        string zipPath = Path.Combine(Path.GetTempPath(), $"TandemHDR-v{available.LatestVersion}.zip");
        string staged = Path.Combine(ExeDir, StagedName);

        try
        {
            using (var response = await Http.GetAsync(available.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                using var file = File.Create(zipPath);
                await response.Content.CopyToAsync(file);
            }

            string actual = Convert.ToHexString(SHA256.HashData(await File.ReadAllBytesAsync(zipPath))).ToLowerInvariant();
            if (actual != available.Sha256)
                return Fail($"The download did not match the published SHA-256 (got {actual})");

            using (var archive = ZipFile.OpenRead(zipPath))
            {
                // Only the exe: the zip also ships a default config.json, which must not
                // land on top of the user's own settings.
                var entry = archive.GetEntry(ExeName)
                    ?? throw new InvalidOperationException($"The release zip contains no {ExeName}");

                File.Delete(staged);
                entry.ExtractToFile(staged);
            }

            Logger.Log($"Staged v{available.LatestVersion} at {staged}");
            return Record(available with { Status = UpdateStatus.Ready });
        }
        catch (Exception ex)
        {
            try { File.Delete(staged); } catch { /* best effort */ }
            return Fail(ex.Message);
        }
        finally
        {
            try { File.Delete(zipPath); } catch { /* best effort */ }
        }
    }

    /// <summary>Swaps the staged exe in and relaunches. A running exe cannot be overwritten
    /// but can be renamed, so the old one is moved aside first and deleted on the next
    /// start. Returns false if the swap failed, leaving this install intact.</summary>
    public static bool ApplyStagedUpdateAndRestart()
    {
        string current = Path.Combine(ExeDir, ExeName);
        string staged = Path.Combine(ExeDir, StagedName);
        string old = Path.Combine(ExeDir, SupersededName);

        if (!File.Exists(staged))
            return false;

        try
        {
            File.Delete(old);
            File.Move(current, old);
        }
        catch (Exception ex)
        {
            Logger.Log($"Update swap failed, install untouched: {ex.Message}");
            return false;
        }

        try
        {
            File.Move(staged, current);
        }
        catch (Exception ex)
        {
            Logger.Log($"Update swap failed after renaming the old exe, rolling back: {ex.Message}");
            File.Move(old, current);
            return false;
        }

        Logger.Log("Update applied, relaunching");
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(current, UpdatedArgument)
        {
            UseShellExecute = true,
            WorkingDirectory = ExeDir,
        });
        return true;
    }

    private static UpdateCheck Fail(string error)
    {
        Logger.Log($"Update check failed: {error}");
        return Record(new UpdateCheck(UpdateStatus.Failed, Error: error));
    }

    private static UpdateCheck Record(UpdateCheck check)
    {
        Last = check;
        return check;
    }
}
