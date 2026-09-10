using System.Security;
using TandemHdr.Native;

namespace TandemHdr.Services;

/// <summary>Raises a Windows toast that carries the tandem discs beside two lines of text.
///
/// The template is one of the old Windows 8 ones on purpose. Both it and the current
/// ToastGeneric template take a picture, but for an app that is not packaged Windows draws
/// the ToastGeneric one in a 16 px slot next to the title, while this template gets a 48 px
/// square of its own with the text to the right of it. Measured on Windows 11, not assumed.
///
/// The App User Model ID comes from the Start Menu shortcut that <see cref="ShortcutHelper"/>
/// writes, which is what lets an unpackaged app raise a toast at all.</summary>
internal static class ToastNotifier
{
    /// <summary>Returns false when Windows refused the toast, so the caller can fall back
    /// to the stock balloon.</summary>
    public static bool Show(string title, string message, bool hdr)
    {
        try
        {
            string logo = new Uri(DiscIcon.EnsureImage(hdr)).AbsoluteUri;
            ToastInterop.Show(ShortcutHelper.AppUserModelId, $"""
                <toast>
                  <visual>
                    <binding template="ToastImageAndText02">
                      <image id="1" src="{logo}"/>
                      <text id="1">{SecurityElement.Escape(title)}</text>
                      <text id="2">{SecurityElement.Escape(message)}</text>
                    </binding>
                  </visual>
                </toast>
                """);
            return true;
        }
        catch (Exception ex)
        {
            Logger.Log($"Toast failed, falling back to the stock balloon: {ex.Message}");
            return false;
        }
    }
}
