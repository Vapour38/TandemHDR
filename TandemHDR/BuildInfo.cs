namespace TandemHdr;

/// <summary>Set by <c>tandem.ps1 test</c> (<c>-p:DevBuild=true</c>). A dev build keeps its own
/// settings and instance lock, so it can never change the installed copy's state.</summary>
internal static class BuildInfo
{
#if DEV
    public const string Suffix = " (Dev)";
#else
    public const string Suffix = "";
#endif

    public static bool IsDev => Suffix.Length > 0;
}
