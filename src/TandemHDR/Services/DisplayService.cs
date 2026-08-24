using System.Runtime.InteropServices;
using TandemHdr.Native;

namespace TandemHdr.Services;

internal class DisplayService
{
    public List<DisplayInfo> EnumerateDisplays()
    {
        var results = new List<DisplayInfo>();

        int err = DisplayConfigApi.GetDisplayConfigBufferSizes(
            QueryDisplayConfigFlags.OnlyActivePaths, out uint pathCount, out uint modeCount);
        if (err != DisplayConfigApi.ERROR_SUCCESS)
        {
            Logger.Log($"GetDisplayConfigBufferSizes failed: {err}");
            return results;
        }

        var paths = new DISPLAYCONFIG_PATH_INFO[pathCount];
        var modes = new DISPLAYCONFIG_MODE_INFO[modeCount];

        err = DisplayConfigApi.QueryDisplayConfig(
            QueryDisplayConfigFlags.OnlyActivePaths,
            ref pathCount, paths, ref modeCount, modes, IntPtr.Zero);
        if (err != DisplayConfigApi.ERROR_SUCCESS)
        {
            Logger.Log($"QueryDisplayConfig failed: {err}");
            return results;
        }

        for (int i = 0; i < pathCount; i++)
        {
            var path = paths[i];

            var name = new DISPLAYCONFIG_TARGET_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DisplayConfigDeviceInfoType.GetTargetName,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_TARGET_DEVICE_NAME>(),
                    adapterId = path.targetInfo.adapterId,
                    id = path.targetInfo.id,
                }
            };
            if (DisplayConfigApi.DisplayConfigGetDeviceInfo(ref name) != DisplayConfigApi.ERROR_SUCCESS)
                continue;

            if (!TryGetHdrCapability(path.targetInfo.adapterId, path.targetInfo.id,
                    out bool supported, out bool enabled))
                continue;

            string friendly = string.IsNullOrWhiteSpace(name.monitorFriendlyDeviceName)
                ? "Display"
                : name.monitorFriendlyDeviceName;

            // GDI name (\\.\DISPLAY1) - needed to open a DC for gamma ramp loading.
            var source = new DISPLAYCONFIG_SOURCE_DEVICE_NAME
            {
                header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
                {
                    type = DisplayConfigDeviceInfoType.GetSourceName,
                    size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SOURCE_DEVICE_NAME>(),
                    adapterId = path.sourceInfo.adapterId,
                    id = path.sourceInfo.id,
                }
            };
            string gdiName = DisplayConfigApi.DisplayConfigGetDeviceInfo(ref source) == DisplayConfigApi.ERROR_SUCCESS
                ? source.viewGdiDeviceName
                : string.Empty;

            results.Add(new DisplayInfo(
                path.targetInfo.adapterId,
                path.targetInfo.id,
                path.sourceInfo.id,
                friendly,
                name.monitorDevicePath,
                gdiName,
                supported,
                enabled));
        }

        return results;
    }

    /// <summary>
    /// Reads HDR support/state for a target. Prefers GET_ADVANCED_COLOR_INFO_2 (24H2+),
    /// which separates HDR from wide-colour gamut; the legacy type-9 query reports
    /// "advanced colour enabled" for a WCG display with HDR switched off, which made the
    /// tray state permanently stick on "HDR". Falls back to type 9 on older builds.
    /// </summary>
    private static bool TryGetHdrCapability(LUID adapterId, uint targetId, out bool supported, out bool enabled)
    {
        supported = false;
        enabled = false;

        var info2 = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayConfigDeviceInfoType.GetAdvancedColorInfo2,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2>(),
                adapterId = adapterId,
                id = targetId,
            }
        };
        if (DisplayConfigApi.DisplayConfigGetDeviceInfo(ref info2) == DisplayConfigApi.ERROR_SUCCESS)
        {
            supported = info2.HighDynamicRangeSupported && !info2.AdvancedColorLimitedByPolicy;
            enabled = info2.HighDynamicRangeUserEnabled
                      || info2.activeColorMode == DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO_2.MODE_HDR;
            return true;
        }

        var info = new DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayConfigDeviceInfoType.GetAdvancedColorInfo,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_GET_ADVANCED_COLOR_INFO>(),
                adapterId = adapterId,
                id = targetId,
            }
        };
        if (DisplayConfigApi.DisplayConfigGetDeviceInfo(ref info) == DisplayConfigApi.ERROR_SUCCESS)
        {
            supported = info.AdvancedColorSupported && !info.AdvancedColorForceDisabled;
            enabled = info.AdvancedColorEnabled;
            return true;
        }

        return false;
    }

    public DisplayInfo? GetPrimaryHdrDisplay()
    {
        var displays = EnumerateDisplays();
        return displays.FirstOrDefault(d => d.HdrSupported) ?? displays.FirstOrDefault();
    }
}
