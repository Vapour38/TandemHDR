using System.IO;
using TandemHdr.Configuration;

namespace TandemHdr.Services;

/// <summary>Readable names for the program list. An executable's own name is often an
/// abbreviation ("bf6", "CollegeFB27"), so a name the launcher recorded is preferred, and
/// the install folder is the fallback: games almost always install into a folder named
/// after themselves.</summary>
internal static class ProgramNames
{
    // Build-layout folders that sit between the install folder and the exe.
    private static readonly HashSet<string> LayoutFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "bin32", "bin64", "binaries", "win32", "win64", "x86", "x64",
        "retail", "game", "content", "release", "shipping", "client",
    };

    public static string DisplayName(WatchedProgram program)
        => string.IsNullOrWhiteSpace(program.Name) ? FromPath(program.Path) : program.Name;

    /// <summary>The nearest parent folder that is not a build-layout folder, or the file
    /// name when there is none.</summary>
    public static string FromPath(string exePath)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(exePath) ?? string.Empty);

        // ponytail: a fixed list of layout folder names; extend it when a real game shows
        // one that is missing.
        while (dir?.Parent != null && LayoutFolders.Contains(dir.Name))
            dir = dir.Parent;

        return dir?.Parent != null ? dir.Name : Path.GetFileNameWithoutExtension(exePath);
    }
}
