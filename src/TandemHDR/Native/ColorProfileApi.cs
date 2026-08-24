using System.Runtime.InteropServices;

namespace TandemHdr.Native;

internal static class ColorProfileApi
{
    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WcsSetDefaultColorProfile(
        WcsProfileManagementScope profileManagementScope,
        [MarshalAs(UnmanagedType.LPWStr)] string? pDeviceName,
        ColorProfileType cptColorProfileType,
        ColorProfileSubType cpstColorProfileSubType,
        uint dwProfileID,
        [MarshalAs(UnmanagedType.LPWStr)] string pProfileName);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int ColorProfileAddDisplayAssociation(
        WcsProfileManagementScope scope,
        [MarshalAs(UnmanagedType.LPWStr)] string profileName,
        LUID targetAdapterID,
        uint sourceID,
        [MarshalAs(UnmanagedType.Bool)] bool setAsDefault,
        [MarshalAs(UnmanagedType.Bool)] bool associateAsAdvancedColor);

    /// <summary>Makes an already-associated profile the display's default for the given colour mode.</summary>
    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern int ColorProfileSetDisplayDefaultAssociation(
        WcsProfileManagementScope scope,
        [MarshalAs(UnmanagedType.LPWStr)] string profileName,
        ColorProfileType cptColorProfileType,
        ColorProfileSubType cpstColorProfileSubType,
        LUID targetAdapterID,
        uint sourceID);

    [DllImport("mscms.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WcsGetCalibrationManagementState([MarshalAs(UnmanagedType.Bool)] out bool enabled);

    [DllImport("mscms.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool WcsSetCalibrationManagementState([MarshalAs(UnmanagedType.Bool)] bool enabled);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InstallColorProfileW(
        [MarshalAs(UnmanagedType.LPWStr)] string? pMachineName,
        [MarshalAs(UnmanagedType.LPWStr)] string pProfileName);

    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AssociateColorProfileWithDeviceW(
        [MarshalAs(UnmanagedType.LPWStr)] string? pMachineName,
        [MarshalAs(UnmanagedType.LPWStr)] string pProfileName,
        [MarshalAs(UnmanagedType.LPWStr)] string pDeviceName);

    /// <summary>Undocumented mscms.dll export that forces Windows to re-read the active calibration.</summary>
    [DllImport("mscms.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool InternalRefreshCalibration(
        [MarshalAs(UnmanagedType.LPWStr)] string? pDeviceName,
        UIntPtr dwFlags,
        IntPtr reserved1,
        IntPtr reserved2);
}
