namespace TandemHdr.Native;

internal enum HdrState
{
    Unsupported,
    Off,
    On,
}

internal enum DisplayConfigDeviceInfoType : uint
{
    GetSourceName = 1,
    GetTargetName = 2,
    GetTargetPreferredMode = 3,
    GetAdapterName = 4,
    SetTargetPersistence = 5,
    GetTargetBaseType = 6,
    GetSupportVirtualResolution = 7,
    SetSupportVirtualResolution = 8,
    GetAdvancedColorInfo = 9,
    SetAdvancedColorState = 10,
    GetSdrWhiteLevel = 11,
    GetAdvancedColorInfo2 = 15,
    SetHdrState = 16,
}

[Flags]
internal enum QueryDisplayConfigFlags : uint
{
    AllPaths = 0x01,
    OnlyActivePaths = 0x02,
    DatabaseCurrent = 0x04,
}

internal enum WcsProfileManagementScope : uint
{
    SystemWide = 0,
    CurrentUser = 1,
}

internal enum ColorProfileType : uint
{
    Icc = 0,
    Dmp = 1,
    Camp = 2,
    Gmmp = 3,
}

internal enum ColorProfileSubType : uint
{
    Perceptual = 0,
    RelativeColorimetric = 1,
    Saturation = 2,
    AbsoluteColorimetric = 3,
    None = 4,
    RgbWorkingSpace = 5,
    CustomWorkingSpace = 6,
    StandardDisplayColorMode = 7,
    ExtendedDisplayColorMode = 8,
}
