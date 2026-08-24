using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;

namespace TandemHdr.Services;

/// <summary>
/// Loads an ICC profile's vcgt (video card gamma table) straight into the GPU.
///
/// Windows associates a profile with a display but only pushes its vcgt to the graphics
/// card when its own calibration loader runs - which in practice means when you press
/// "set profile" in Colour Management. That is why an SDR gamma-correction profile can
/// show as the default and still have no visible effect. Loading the ramp ourselves is
/// what DisplayCAL's profile loader does, and it takes effect immediately.
///
/// HDR profiles use an MHC2 tag instead of a vcgt; those are applied by Windows' own
/// pipeline, so for them we just reset the ramp to linear so a stale SDR curve is not
/// left stacked on top.
/// </summary>
internal static class GammaService
{
    private const int RampEntries = 256;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateDCW(string? driver, string device, string? output, IntPtr initData);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

    [DllImport("gdi32.dll")]
    private static extern bool GetDeviceGammaRamp(IntPtr hdc, ushort[] ramp);

    /// <summary>Loads the profile's vcgt curve, or a linear ramp if it has none.</summary>
    public static bool ApplyProfileGamma(string gdiDeviceName, string profilePath, out bool hadVcgt)
    {
        ushort[]? ramp = TryReadVcgt(profilePath);
        hadVcgt = ramp != null;
        ramp ??= LinearRamp();
        return SetRamp(gdiDeviceName, ramp);
    }

    public static bool SetLinear(string gdiDeviceName) => SetRamp(gdiDeviceName, LinearRamp());

    private static bool SetRamp(string gdiDeviceName, ushort[] ramp)
    {
        if (string.IsNullOrEmpty(gdiDeviceName))
        {
            Logger.Log("No GDI device name available; cannot load gamma ramp");
            return false;
        }

        IntPtr hdc = CreateDCW(null, gdiDeviceName, null, IntPtr.Zero);
        if (hdc == IntPtr.Zero)
        {
            Logger.Log($"CreateDC failed for '{gdiDeviceName}'");
            return false;
        }

        try
        {
            if (!SetDeviceGammaRamp(hdc, ramp))
            {
                Logger.Log($"SetDeviceGammaRamp failed for '{gdiDeviceName}'");
                return false;
            }

            // Windows silently clamps ramps it considers too aggressive unless the
            // ICM\GdiIcmGammaRange policy is widened, so confirm what actually landed.
            var readBack = new ushort[RampEntries * 3];
            if (GetDeviceGammaRamp(hdc, readBack) && !RampsMatch(ramp, readBack))
                Logger.Log("Warning: the loaded gamma ramp was clamped by Windows " +
                           "(set HKLM\\SOFTWARE\\Microsoft\\Windows NT\\CurrentVersion\\ICM\\GdiIcmGammaRange = 256 to allow the full range)");

            return true;
        }
        finally
        {
            DeleteDC(hdc);
        }
    }

    private static bool RampsMatch(ushort[] a, ushort[] b)
    {
        for (int i = 0; i < a.Length; i++)
        {
            // Drivers round the low bits; only flag meaningful divergence.
            if (Math.Abs(a[i] - b[i]) > 256) return false;
        }
        return true;
    }

    private static ushort[] LinearRamp()
    {
        var ramp = new ushort[RampEntries * 3];
        for (int i = 0; i < RampEntries; i++)
        {
            ushort v = (ushort)(i * 257);
            ramp[i] = v;
            ramp[RampEntries + i] = v;
            ramp[RampEntries * 2 + i] = v;
        }
        return ramp;
    }

    /// <summary>
    /// Extracts the vcgt tag as a 3x256 ramp in the layout GDI expects, or null if the
    /// profile has no vcgt (e.g. an MHC2-based HDR profile).
    /// </summary>
    private static ushort[]? TryReadVcgt(string profilePath)
    {
        try
        {
            byte[] data = File.ReadAllBytes(profilePath);
            if (data.Length < 132) return null;

            uint tagCount = ReadU32(data, 128);
            if (tagCount > 1024) return null;

            for (int i = 0; i < tagCount; i++)
            {
                int entry = 132 + i * 12;
                if (entry + 12 > data.Length) return null;

                string sig = System.Text.Encoding.ASCII.GetString(data, entry, 4);
                if (sig != "vcgt") continue;

                int offset = (int)ReadU32(data, entry + 4);
                int size = (int)ReadU32(data, entry + 8);
                if (offset < 0 || size < 18 || offset + size > data.Length) return null;

                return ParseVcgt(data, offset, size);
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to read vcgt from '{profilePath}': {ex.Message}");
        }

        return null;
    }

    private static ushort[]? ParseVcgt(byte[] data, int offset, int size)
    {
        // Tag data: 'vcgt' (4) + reserved (4) + gammaType (4), then the payload.
        uint gammaType = ReadU32(data, offset + 8);
        if (gammaType != 0)
        {
            Logger.Log("vcgt uses the formula encoding, which is not supported; using linear ramp");
            return null;
        }

        int p = offset + 12;
        int channels = ReadU16(data, p);
        int entryCount = ReadU16(data, p + 2);
        int entrySize = ReadU16(data, p + 4);
        p += 6;

        if (channels != 3 || entryCount <= 0 || (entrySize != 1 && entrySize != 2))
        {
            Logger.Log($"Unsupported vcgt layout: channels={channels} entries={entryCount} entrySize={entrySize}");
            return null;
        }
        if (p + channels * entryCount * entrySize > data.Length) return null;

        var ramp = new ushort[RampEntries * 3];
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < RampEntries; i++)
            {
                // Resample if the profile stores a table of a different length.
                int src = entryCount == RampEntries
                    ? i
                    : (int)((long)i * (entryCount - 1) / (RampEntries - 1));

                int at = p + (c * entryCount + src) * entrySize;
                ramp[c * RampEntries + i] = entrySize == 2
                    ? ReadU16(data, at)
                    : (ushort)(data[at] * 257);
            }
        }

        return ramp;
    }

    private static uint ReadU32(byte[] d, int o) => BinaryPrimitives.ReadUInt32BigEndian(d.AsSpan(o, 4));
    private static ushort ReadU16(byte[] d, int o) => BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(o, 2));
}
