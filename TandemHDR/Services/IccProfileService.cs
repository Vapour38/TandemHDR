using System.IO;
using System.Runtime.InteropServices;
using TandemHdr.Native;

namespace TandemHdr.Services;

internal class IccProfileService(DisplayService displayService)
{
    /// <summary>Windows' per-user color profile store. Exposed so callers (e.g. the
    /// settings UI) can tell whether a configured profile is still resolvable.</summary>
    public static readonly string SystemColorDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "spool", "drivers", "color");

    /// <summary>True if the given profile path is still resolvable, either at its
    /// original location or already installed under the system color directory.</summary>
    public static bool ProfileExists(string? profilePath)
    {
        if (string.IsNullOrWhiteSpace(profilePath))
            return false;

        return File.Exists(profilePath) || File.Exists(Path.Combine(SystemColorDir, Path.GetFileName(profilePath)));
    }

    /// <summary>
    /// Applies the ICC profile for the given HDR state.
    /// Returns the profile filename that was applied, or null if none.
    /// </summary>
    public string? ApplyProfileForState(HdrState state, string? sdrProfilePath, string? hdrProfilePath)
    {
        var display = displayService.GetPrimaryHdrDisplay();
        if (display == null)
        {
            Logger.Log("No HDR-capable display found for profile application");
            return null;
        }

        switch (state)
        {
            case HdrState.Off when !string.IsNullOrEmpty(sdrProfilePath):
                return ApplySdrProfile(display, sdrProfilePath);
            case HdrState.On when !string.IsNullOrEmpty(hdrProfilePath):
                return ApplyHdrProfile(display, hdrProfilePath);
            default:
                Logger.Log($"No profile configured for state {state}");
                return null;
        }
    }

    private string? ApplySdrProfile(DisplayInfo display, string profilePath)
        => Apply(display, profilePath, advancedColor: false);

    private string? ApplyHdrProfile(DisplayInfo display, string profilePath)
        => Apply(display, profilePath, advancedColor: true);

    /// <summary>
    /// Associates the profile with the display and makes it the default for that colour
    /// mode. Windows keeps two independent default slots per display — SDR
    /// (standard display colour mode) and advanced colour (used in HDR) — so the
    /// advancedColor flag selects which one this profile becomes the default for.
    /// </summary>
    private string? Apply(DisplayInfo display, string profilePath, bool advancedColor)
    {
        string label = advancedColor ? "HDR" : "SDR";

        string profileName = EnsureInstalled(profilePath);
        if (profileName == null!) return null;

        int result = ColorProfileApi.ColorProfileAddDisplayAssociation(
            WcsProfileManagementScope.CurrentUser,
            profileName,
            display.AdapterId,
            display.SourceId,
            setAsDefault: true,
            associateAsAdvancedColor: advancedColor);

        if (result != 0)
        {
            Logger.Log($"ColorProfileAddDisplayAssociation failed for {label}: 0x{result:X8}");

            if (!ColorProfileApi.AssociateColorProfileWithDeviceW(null, profileName, display.MonitorDevicePath))
            {
                Logger.Log($"AssociateColorProfileWithDeviceW {label} fallback failed: error {Marshal.GetLastWin32Error()}");
                return null;
            }
        }

        // Adding an association does not reliably make it the active default, which is
        // why the profile could look "associated" in Settings while nothing changed
        // on screen. Set the default for the colour mode explicitly.
        var subType = advancedColor
            ? ColorProfileSubType.ExtendedDisplayColorMode
            : ColorProfileSubType.StandardDisplayColorMode;

        int def = ColorProfileApi.ColorProfileSetDisplayDefaultAssociation(
            WcsProfileManagementScope.CurrentUser,
            profileName,
            ColorProfileType.Icc,
            subType,
            display.AdapterId,
            display.SourceId);

        if (def != 0)
        {
            Logger.Log($"ColorProfileSetDisplayDefaultAssociation failed for {label}: 0x{def:X8}");
            return null;
        }

        RefreshCalibration();

        // Windows sets the association but does not push the profile's vcgt curve to the
        // GPU until its calibration loader runs, so load it ourselves.
        string installedPath = Path.Combine(SystemColorDir, profileName);
        if (GammaService.ApplyProfileGamma(display.GdiDeviceName, installedPath, out bool hadVcgt))
            Logger.Log(hadVcgt
                ? $"Loaded vcgt gamma curve from '{profileName}'"
                : $"'{profileName}' has no vcgt (MHC2/HDR profile); reset gamma ramp to linear");

        Logger.Log($"{label} profile '{profileName}' applied to {display.MonitorName}");
        return profileName;
    }

    private static string EnsureInstalled(string profilePath)
    {
        string fileName = Path.GetFileName(profilePath);
        string systemPath = Path.Combine(SystemColorDir, fileName);

        if (File.Exists(systemPath))
            return fileName;

        if (!File.Exists(profilePath))
        {
            Logger.Log($"Profile file not found: {profilePath}");
            return null!;
        }

        bool ok = ColorProfileApi.InstallColorProfileW(null, profilePath);
        if (!ok)
        {
            int err = Marshal.GetLastWin32Error();
            Logger.Log($"InstallColorProfileW failed for '{profilePath}': error {err}");
            return null!;
        }

        Logger.Log($"Installed profile '{fileName}' to system color directory");
        return fileName;
    }

    /// <summary>
    /// Forces Windows to re-load the display calibration so the new default profile's
    /// gamma/MHC2 data actually reaches the GPU. Toggling calibration management is the
    /// reliable trigger; the undocumented InternalRefreshCalibration export returns
    /// failure on this build, so it is only a best-effort extra nudge.
    /// </summary>
    private static void RefreshCalibration()
    {
        try
        {
            if (ColorProfileApi.WcsGetCalibrationManagementState(out bool enabled) && enabled)
            {
                ColorProfileApi.WcsSetCalibrationManagementState(false);
                ColorProfileApi.WcsSetCalibrationManagementState(true);
            }
            else
            {
                // Calibration loading was off entirely - turn it on, otherwise Windows
                // ignores the profile's calibration data.
                if (!ColorProfileApi.WcsSetCalibrationManagementState(true))
                    Logger.Log($"WcsSetCalibrationManagementState(true) failed: error {Marshal.GetLastWin32Error()}");
                else
                    Logger.Log("Enabled Windows display calibration management");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"RefreshCalibration failed: {ex.Message}");
        }

        try
        {
            ColorProfileApi.InternalRefreshCalibration(null, UIntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        }
        catch
        {
            // Undocumented and non-essential.
        }
    }
}
