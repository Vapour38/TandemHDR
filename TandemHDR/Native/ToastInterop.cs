using System.Runtime.InteropServices;

namespace TandemHdr.Native;

/// <summary>Raises a Windows toast through the WinRT activation API by hand.
///
/// The alternative is to target net10.0-windows10.0.19041.0, which references the whole
/// Windows SDK projection and takes the published exe from 0.7 MB to 26 MB for the three
/// calls below. Trimming that assembly needs a self-contained publish, and CsWinRT's
/// namespace filter pulls in the same closure again, so the projection is written out here
/// instead: the interfaces are declared down to the one method each that is used, with the
/// three IInspectable slots ahead of them because every WinRT interface carries those.
///
/// A wrong interface id fails at once with E_NOINTERFACE rather than misbehaving.</summary>
internal static class ToastInterop
{
    private static readonly Guid IidXmlDocument = new("f7f3a506-1e87-42d6-bcfb-b8c809fa5494");
    private static readonly Guid IidXmlDocumentIo = new("6cd0e74e-ee65-4489-9ebf-ca43e87ba637");
    private static readonly Guid IidToastManagerStatics = new("50ac103f-d235-4598-bbef-98fe4d1a3ad4");
    private static readonly Guid IidToastFactory = new("04124b20-82c6-4229-b109-fd9ed4662b53");

    [DllImport("combase.dll", CharSet = CharSet.Unicode)]
    private static extern int WindowsCreateString(string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll")]
    private static extern int RoActivateInstance(IntPtr activatableClassId, out IntPtr instance);

    [DllImport("combase.dll")]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [ComImport, Guid("6cd0e74e-ee65-4489-9ebf-ca43e87ba637")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IXmlDocumentIo
    {
        void GetIids(out uint count, out IntPtr iids);
        void GetRuntimeClassName(out IntPtr className);
        void GetTrustLevel(out int trustLevel);
        void LoadXml(IntPtr xml);
    }

    [ComImport, Guid("50ac103f-d235-4598-bbef-98fe4d1a3ad4")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotificationManagerStatics
    {
        void GetIids(out uint count, out IntPtr iids);
        void GetRuntimeClassName(out IntPtr className);
        void GetTrustLevel(out int trustLevel);
        void CreateToastNotifier(out IntPtr notifier);
        void CreateToastNotifierWithId(IntPtr applicationId, out IntPtr notifier);
    }

    [ComImport, Guid("04124b20-82c6-4229-b109-fd9ed4662b53")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotificationFactory
    {
        void GetIids(out uint count, out IntPtr iids);
        void GetRuntimeClassName(out IntPtr className);
        void GetTrustLevel(out int trustLevel);
        void CreateToastNotification(IntPtr content, out IntPtr notification);
    }

    [ComImport, Guid("75927b93-03f3-41ec-91d3-6e5bac1b38e7")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IToastNotifier
    {
        void GetIids(out uint count, out IntPtr iids);
        void GetRuntimeClassName(out IntPtr className);
        void GetTrustLevel(out int trustLevel);
        void Show(IntPtr notification);
    }

    /// <summary>Throws on any failed call, which the caller turns into the fallback balloon.</summary>
    public static void Show(string appUserModelId, string toastXml)
    {
        var strings = new List<IntPtr>();
        var comObjects = new List<IntPtr>();

        IntPtr NewString(string value)
        {
            Check(WindowsCreateString(value, value.Length, out IntPtr handle));
            strings.Add(handle);
            return handle;
        }

        IntPtr Keep(IntPtr pointer)
        {
            comObjects.Add(pointer);
            return pointer;
        }

        try
        {
            Check(RoActivateInstance(NewString("Windows.Data.Xml.Dom.XmlDocument"), out IntPtr documentInstance));
            Keep(documentInstance);
            ((IXmlDocumentIo)Marshal.GetObjectForIUnknown(documentInstance)).LoadXml(NewString(toastXml));

            // The toast factory wants an IXmlDocument, not the IInspectable it was activated as.
            Check(Marshal.QueryInterface(documentInstance, in IidXmlDocument, out IntPtr document));
            Keep(document);

            var managerIid = IidToastManagerStatics;
            Check(RoGetActivationFactory(NewString("Windows.UI.Notifications.ToastNotificationManager"),
                ref managerIid, out IntPtr manager));
            Keep(manager);
            ((IToastNotificationManagerStatics)Marshal.GetObjectForIUnknown(manager))
                .CreateToastNotifierWithId(NewString(appUserModelId), out IntPtr notifier);
            Keep(notifier);

            var factoryIid = IidToastFactory;
            Check(RoGetActivationFactory(NewString("Windows.UI.Notifications.ToastNotification"),
                ref factoryIid, out IntPtr factory));
            Keep(factory);
            ((IToastNotificationFactory)Marshal.GetObjectForIUnknown(factory))
                .CreateToastNotification(document, out IntPtr toast);
            Keep(toast);

            ((IToastNotifier)Marshal.GetObjectForIUnknown(notifier)).Show(toast);
        }
        finally
        {
            foreach (IntPtr pointer in comObjects) Marshal.Release(pointer);
            foreach (IntPtr handle in strings) WindowsDeleteString(handle);
        }
    }

    private static void Check(int hr) => Marshal.ThrowExceptionForHR(hr);
}
