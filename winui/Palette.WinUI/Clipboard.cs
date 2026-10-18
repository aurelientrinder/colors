using Windows.ApplicationModel.DataTransfer;

namespace Palette;

/// <summary>
/// What was copied, for the engine's "clip" search (clip.rs): Windows' clipboard history (Win+V) at
/// start and when the palette opens, and each copy made while Palette runs. It goes to the engine's
/// memory only; nothing is written down. Copies a password manager marks as private are skipped.
/// </summary>
public sealed partial class MainWindow
{
    private DateTime _clipRead = DateTime.MinValue;
    private int _clipBusy;

    private void StartClipboard()
    {
        try { Windows.ApplicationModel.DataTransfer.Clipboard.ContentChanged += (_, _) => _ = ReadCopied(); }
        catch (Exception ex) { App.Log("no clipboard changes: " + ex.Message); }
        _ = ReadClipboardHistory();
    }

    /// <summary>The history Windows keeps (the last 25 copies), when it is turned on in Settings.</summary>
    private async Task ReadClipboardHistory()
    {
        if (!_settings.ClipboardHistory || Interlocked.Exchange(ref _clipBusy, 1) == 1) return;
        try
        {
            _clipRead = DateTime.UtcNow;
            var found = await Windows.ApplicationModel.DataTransfer.Clipboard.GetHistoryItemsAsync();
            if (found.Status != ClipboardHistoryItemsResultStatus.Success) return;
            // Newest first in the result; the engine wants oldest first so the newest ends in front.
            foreach (var item in found.Items.Reverse())
            {
                if (!item.Content.Contains(StandardDataFormats.Text)) continue;
                string text = await item.Content.GetTextAsync();
                Native.ClipAdd(text, item.Timestamp.ToUnixTimeSeconds());
            }
        }
        catch (Exception ex)
        {
            App.Log("clipboard history unavailable: " + ex.Message);
        }
        finally
        {
            Volatile.Write(ref _clipBusy, 0);
        }
    }

    /// <summary>A copy was just made.</summary>
    private async Task ReadCopied()
    {
        if (!_settings.ClipboardHistory) return;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var view = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (view.Contains("ExcludeClipboardContentFromMonitorProcessing") || !view.Contains(StandardDataFormats.Text)) return;
                Native.ClipAdd(await view.GetTextAsync(), DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                return;
            }
            catch
            {
                await Task.Delay(150); // the copying program still has the clipboard open
            }
        }
    }
}
