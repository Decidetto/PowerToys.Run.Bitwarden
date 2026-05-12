using System.Runtime.InteropServices;
using System.Windows;
using Wox.Plugin.Logger;

namespace PowerToys.Run.Bitwarden.Security;

internal static class SecureClipboard
{
    private const string ExcludeFormatName = "ExcludeClipboardContentFromMonitorProcessing";
    private static readonly TimeSpan DefaultAutoClearDelay = TimeSpan.FromSeconds(30);

    static SecureClipboard()
    {
        // Register the format eagerly so it's available the first time we set text.
        _ = RegisterClipboardFormat(ExcludeFormatName);
    }

    public static void SetText(string text, TimeSpan? autoClearAfter = null)
    {
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;

            dispatcher.Invoke(() =>
            {
                var data = new DataObject();
                data.SetText(text);
                // Excludes the content from Win+V history and cloud-clipboard sync.
                data.SetData(ExcludeFormatName, new byte[] { 0 });
                Clipboard.SetDataObject(data, copy: true);
            });

            _ = ScheduleClearAsync(text, autoClearAfter ?? DefaultAutoClearDelay);
        }
        catch (Exception ex)
        {
            Log.Exception("SecureClipboard.SetText failed", ex, typeof(SecureClipboard));
        }
    }

    private static async Task ScheduleClearAsync(string previous, TimeSpan ttl)
    {
        await Task.Delay(ttl).ConfigureAwait(false);
        try
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null) return;
            dispatcher.Invoke(() =>
            {
                // Only clear if the clipboard still holds what we put there.
                if (Clipboard.ContainsText() && Clipboard.GetText() == previous)
                    Clipboard.Clear();
            });
        }
        catch
        {
            // Clipboard may be locked by another app; let the next set/clear cycle handle it.
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string lpszFormat);
}
