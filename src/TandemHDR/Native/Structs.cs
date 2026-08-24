using System.Runtime.InteropServices;

namespace TandemHdr.Native;

[StructLayout(LayoutKind.Sequential)]
internal struct LUID
{
    public uint LowPart;
    public int HighPart;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_RATIONAL
{
    public uint Numerator;
    public uint Denominator;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_SOURCE_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_TARGET_INFO
{
    public LUID adapterId;
    public uint id;
    public uint modeInfoIdx;
    public uint outputTechnology;
    public uint rotation;
    public uint scaling;
    public DISPLAYCONFIG_RATIONAL refreshRate;
    public uint scanLineOrdering;
    [MarshalAs(UnmanagedType.Bool)] public bool targetAvailable;
    public uint statusFlags;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_PATH_INFO
{
    public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo;
    public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo;
    public uint flags;
}

/// <summary>Opaque 48-byte union; we never read its contents.</summary>
[StructLayout(LayoutKind.Sequential, Size = 48)]
internal struct DISPLAYCONFIG_MODE_INFO_UNION
{
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_MODE_INFO
{
    public uint infoType;
    public uint id;
    public LUID adapterId;
    public DISPLAYCONFIG_MODE_INFO_UNION modeInfo;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_DEVICE_INFO_HEADER
{
    public DisplayConfigDeviceInfoType type;
    public uint size;
    public LUID adapterId;
    public uint id;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_TARGET_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint flags;
    public uint outputTechnology;
    public ushort edidManufactureId;
    public ushort edidProductCodeId;
    public uint connectorInstance;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string monitorFriendlyDeviceName;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string monitorDevicePath;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint value;
    public uint colorEncoding;
    public uint bitsPerColorChannel;

    public bool AdvancedColorSupported => (value & 0x1) != 0;
    public bool AdvancedColorEnabled => (value & 0x2) != 0;
    public bool AdvancedColorForceDisabled => (value & 0x8) != 0;
}

/// <summary>
/// Win11 24H2+ replacement for GET_ADVANCED_COLOR_INFO. The older query conflates HDR
/// with wide-colour gamut / Auto Colour Management, so it reports "enabled" on a WCG
/// display even when HDR is off. This one distinguishes them.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint value;
    public uint colorEncoding;
    public uint bitsPerColorChannel;
    public uint activeColorMode; // 0 = SDR, 1 = WCG, 2 = HDR

    public bool AdvancedColorSupported => (value & 0x1) != 0;
    public bool AdvancedColorLimitedByPolicy => (value & 0x8) != 0;
    public bool HighDynamicRangeSupported => (value & 0x10) != 0;
    public bool HighDynamicRangeUserEnabled => (value & 0x20) != 0;

    public const uint MODE_HDR = 2;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint value;
}

[StructLayout(LayoutKind.Sequential)]
internal struct DISPLAYCONFIG_SET_HDR_STATE
{
    public DISPLAYCONFIG_DEVICE_INFO_HEADER header;
    public uint value;
}

internal record DisplayInfo(
    LUID AdapterId,
    uint TargetId,
    uint SourceId,
    string MonitorName,
    string MonitorDevicePath,
    string GdiDeviceName,
    bool HdrSupported,
    bool HdrEnabled);
