using Microsoft.UI.Xaml;

namespace Palette;

/// <summary>
/// The passage under a document found by meaning: the sentence that answered, the matching words
/// in bold. "when does my lease end" shows the sentence, not just a file name. Asked for once the
/// related rows are on screen, off the UI thread (the helper may have to read a PDF), and drawn
/// when it arrives; a row whose file or query has moved on drops it.
/// </summary>
public sealed partial class MainWindow
{
    private void RequestPassages(int version, string query)
    {
        if (query.Length == 0) return;
        for (int i = 0; i < Items.Count; i++)
        {
            if (Items[i] is not { Related: true, Path: { } path } vm) continue;
            string key = path + "\n" + query;
            if (vm.PassageKey == key) continue;
            _ = Task.Run(() =>
            {
                var r = Native.Passage(path, query);
                if (r == null || r.Text.Length == 0) return;
                DispatcherQueue.TryEnqueue(() => ApplyPassage(version, vm, path, key, r));
            });
        }
    }

    private void ApplyPassage(int version, HitVm vm, string path, string key, Native.PassageReply r)
    {
        if (version != _queryVersion || !_visible || _view is View.Actions or View.Done or View.Settings or View.Chat or View.Chats) return;
        if (vm.Path != path || !vm.Related || !Items.Contains(vm)) return;
        vm.SetPassage(key, r.Text, r.Marks);
        HoldHeight();
        Resize(recenter: false);
    }
}
