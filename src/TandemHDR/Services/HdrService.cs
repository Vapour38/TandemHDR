using System.Runtime.InteropServices;
using TandemHdr.Native;

namespace TandemHdr.Services;

internal class HdrService(DisplayService displayService)
{
    public HdrState GetHdrState()
    {
        var display = displayService.GetPrimaryHdrDisplay();
        if (display == null || !display.HdrSupported)
            return HdrState.Unsupported;

        return display.HdrEnabled ? HdrState.On : HdrState.Off;
    }

    /// <summary>Flips HDR on the primary HDR-capable display and returns the state that actually took effect.</summary>
    public HdrState ToggleHdr()
    {
        var display = displayService.GetPrimaryHdrDisplay();
        if (display == null || !display.HdrSupported)
            return HdrState.Unsupported;

        return Apply(display, !display.HdrEnabled);
    }

    /// <summary>Drives HDR to a specific state rather than flipping it. Used by the program
    /// watcher, which knows the state it wants and must not care what the state is now.
    /// A no-op (returning the current state) when HDR is already where it should be.</summary>
    public HdrState SetHdr(bool enable)
    {
        var display = displayService.GetPrimaryHdrDisplay();
        if (display == null || !display.HdrSupported)
            return HdrState.Unsupported;

        if (display.HdrEnabled == enable)
            return enable ? HdrState.On : HdrState.Off;

        return Apply(display, enable);
    }

    private HdrState Apply(DisplayInfo display, bool enable)
    {
        Logger.Log($"Setting HDR to {(enable ? "ON" : "OFF")} on {display.MonitorName}");

        if (!TrySetHdrState(display, enable))
            Logger.Log("Both SetHdrState and SetAdvancedColorState reported failure");

        var state = WaitForState(enable);
        if (state == (enable ? HdrState.On : HdrState.Off))
        {
            Logger.Log($"HDR state after toggle: {state}");
            return state;
        }

        // The API reported success but the driver did not apply it. Retry once through
        // the other entry point before giving up — this is the case that used to leave
        // the tray stuck showing HDR on.
        Logger.Log($"HDR state did not change (still {state}); retrying with the alternate API");
        TrySetHdrState(display, enable, preferLegacy: true);
        state = WaitForState(enable);
        Logger.Log($"HDR state after retry: {state}");
        return state;
    }

    private static bool TrySetHdrState(DisplayInfo display, bool enable, bool preferLegacy = false)
    {
        if (!preferLegacy && SetHdrState(display, enable))
            return true;

        if (SetAdvancedColorState(display, enable))
            return true;

        return !preferLegacy && SetHdrState(display, enable);
    }

    private static bool SetHdrState(DisplayInfo display, bool enable)
    {
        var packet = new DISPLAYCONFIG_SET_HDR_STATE
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayConfigDeviceInfoType.SetHdrState,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_HDR_STATE>(),
                adapterId = display.AdapterId,
                id = display.TargetId,
            },
            value = enable ? 1u : 0u,
        };

        int result = DisplayConfigApi.DisplayConfigSetDeviceInfo(ref packet);
        if (result == DisplayConfigApi.ERROR_SUCCESS)
        {
            Logger.Log("SetHdrState (type 16) succeeded");
            return true;
        }

        Logger.Log($"SetHdrState (type 16) failed: {result}");
        return false;
    }

    private static bool SetAdvancedColorState(DisplayInfo display, bool enable)
    {
        var packet = new DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE
        {
            header = new DISPLAYCONFIG_DEVICE_INFO_HEADER
            {
                type = DisplayConfigDeviceInfoType.SetAdvancedColorState,
                size = (uint)Marshal.SizeOf<DISPLAYCONFIG_SET_ADVANCED_COLOR_STATE>(),
                adapterId = display.AdapterId,
                id = display.TargetId,
            },
            value = enable ? 1u : 0u,
        };

        int result = DisplayConfigApi.DisplayConfigSetDeviceInfo(ref packet);
        if (result == DisplayConfigApi.ERROR_SUCCESS)
        {
            Logger.Log("SetAdvancedColorState (type 10) succeeded");
            return true;
        }

        Logger.Log($"SetAdvancedColorState (type 10) failed: {result}");
        return false;
    }

    /// <summary>
    /// The display pipeline takes a moment to re-signal after an HDR change, so poll
    /// briefly rather than trusting a single immediate read.
    /// </summary>
    private HdrState WaitForState(bool expectOn, int timeoutMs = 2000)
    {
        var target = expectOn ? HdrState.On : HdrState.Off;
        var deadline = Environment.TickCount64 + timeoutMs;
        HdrState state;

        do
        {
            state = GetHdrState();
            if (state == target || state == HdrState.Unsupported)
                return state;
            Thread.Sleep(100);
        }
        while (Environment.TickCount64 < deadline);

        return state;
    }
}
