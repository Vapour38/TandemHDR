using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace TandemHdr.Services;

/// <summary>
/// Creates a Start Menu shortcut with an AppUserModelID so that Windows toast
/// notifications display the correct app name and icon.
/// </summary>
internal static class ShortcutHelper
{
    public const string AppUserModelId = "TandemHdr.Desktop";
    private const string ShortcutName = "Tandem HDR.lnk";

    [DllImport("shell32.dll", SetLastError = true)]
    public static extern int SetCurrentProcessExplicitAppUserModelID(
        [MarshalAs(UnmanagedType.LPWStr)] string AppID);

    public static void EnsureShortcut()
    {
        string shortcutPath = GetShortcutPath();

        // Re-create if missing or pointing to wrong exe
        if (File.Exists(shortcutPath))
        {
            try
            {
                string? target = ReadShortcutTarget(shortcutPath);
                string exePath = Environment.ProcessPath ?? Application.ExecutablePath;
                if (string.Equals(target, exePath, StringComparison.OrdinalIgnoreCase))
                    return; // Already correct
            }
            catch { /* recreate */ }
        }

        try
        {
            CreateShortcut(shortcutPath);
            Logger.Log($"Start Menu shortcut created: {shortcutPath}");
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to create Start Menu shortcut: {ex.Message}");
        }
    }

    private static string GetShortcutPath()
    {
        string startMenu = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Microsoft", "Windows", "Start Menu", "Programs");
        return Path.Combine(startMenu, ShortcutName);
    }

    private static void CreateShortcut(string shortcutPath)
    {
        string exePath = Environment.ProcessPath ?? Application.ExecutablePath;

        var shellLink = (IShellLinkW)new CShellLink();
        shellLink.SetPath(exePath);
        shellLink.SetWorkingDirectory(Path.GetDirectoryName(exePath)!);
        shellLink.SetDescription("Tandem HDR - Toggle HDR and ICC profiles");

        // Set the AppUserModelID property on the shortcut
        var propertyStore = (IPropertyStore)shellLink;
        var appIdKey = new PROPERTYKEY
        {
            fmtid = new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"),
            pid = 5
        };
        var propVar = new PROPVARIANT();
        propVar.SetString(AppUserModelId);
        propertyStore.SetValue(ref appIdKey, ref propVar);
        propertyStore.Commit();

        // Save the shortcut
        var persistFile = (IPersistFile)shellLink;
        persistFile.Save(shortcutPath, true);

        propVar.Clear();
    }

    private static string? ReadShortcutTarget(string shortcutPath)
    {
        var shellLink = (IShellLinkW)new CShellLink();
        var persistFile = (IPersistFile)shellLink;
        persistFile.Load(shortcutPath, 0);
        var sb = new StringBuilder(260);
        shellLink.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
        return sb.ToString();
    }

    #region COM interop for IShellLink

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class CShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cch, IntPtr pfd, uint fFlags);
        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out ushort pwHotkey);
        void SetHotkey(ushort wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath,
            int cch, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        void GetCount(out uint cProps);
        void GetAt(uint iProp, out PROPERTYKEY pkey);
        void GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        void SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);
        void Commit();
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort wReserved1;
        public ushort wReserved2;
        public ushort wReserved3;
        public IntPtr pwszVal;

        public void SetString(string value)
        {
            vt = 31; // VT_LPWSTR
            pwszVal = Marshal.StringToCoTaskMemUni(value);
        }

        public void Clear()
        {
            if (vt == 31 && pwszVal != IntPtr.Zero)
            {
                Marshal.FreeCoTaskMem(pwszVal);
                pwszVal = IntPtr.Zero;
            }
            vt = 0;
        }
    }

    #endregion
}
