using System.IO.Compression;
using System.Text;
using System.Xml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;

namespace Palette;

/// <summary>
/// Preview of the selected result, shown when Tab is pressed on a file: images and the first
/// page of PDFs are rendered, text, code and Word documents show their opening text, anything
/// else gets the shell's thumbnail, with the file's folder and size below. It opens inside the
/// selected row's card and follows the selection until Tab is pressed again or the query changes.
/// A calculator row always shows its answer there. Loading is asynchronous and dropped as soon as
/// the selection moves on.
/// </summary>
public sealed partial class MainWindow
{
    private string? _pvPath;      // file the preview shows (or is loading)
    private int _pvVersion;       // bumped on every change; stale loads check it and stop
    private HitVm? _pvVm;         // the row the preview pane sits in (kept while it closes)
    private double _pvFull = FilePaneDip;  // the pane's open height
    private bool _previewOn;      // Tab asked for file previews
    // Rows the pane just left: their room closes while it opens in the next row (u runs 1 to 0).
    private readonly List<(HitVm vm, double from, double u)> _pvClosing = new();

    // The pane under the row: a 6 gap, the box (8 + 186 picture + two 52 rows + divider + 4) and
    // 12 below; the answer box is 172.
    private const double FilePaneDip = 6 + 8 + 186 + 52 + 1 + 52 + 4 + 12, AnswerPaneDip = 6 + 172 + 12;

    private const long MaxImageBytes = 80L << 20;
    private const int MaxTextBytes = 24 << 10;
    private const int MaxTextLines = 300;

    private static readonly HashSet<string> ImageExt = new(StringComparer.OrdinalIgnoreCase)
        { ".png", ".jpg", ".jpeg", ".jfif", ".gif", ".bmp", ".webp", ".ico", ".tif", ".tiff", ".heic", ".avif" };

    private static readonly HashSet<string> ProseExt = new(StringComparer.OrdinalIgnoreCase)
        { ".txt", ".md", ".markdown", ".rst", ".log", ".tex", ".docx", ".rtf" };

    private static readonly HashSet<string> TextExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".markdown", ".rst", ".log", ".tex", ".csv", ".tsv", ".json", ".jsonc", ".xml", ".yaml", ".yml",
        ".toml", ".ini", ".cfg", ".conf", ".env", ".gitignore", ".editorconfig", ".html", ".htm", ".css", ".scss",
        ".js", ".mjs", ".cjs", ".ts", ".tsx", ".jsx", ".vue", ".svelte", ".py", ".rs", ".go", ".java", ".kt", ".swift",
        ".c", ".h", ".cpp", ".hpp", ".cc", ".cs", ".fs", ".vb", ".rb", ".php", ".lua", ".r", ".m", ".sql", ".sh",
        ".bash", ".zsh", ".ps1", ".psm1", ".bat", ".cmd", ".xaml", ".csproj", ".sln", ".props", ".targets", ".svg",
        ".ipynb", ".dart", ".scala", ".pl", ".hs", ".ex", ".exs", ".clj", ".zig", ".nim", ".srt", ".vtt",
    };

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        foreach (var o in e.RemovedItems) if (o is HitVm v) { v.Selected = false; v.Confirming = false; }
        foreach (var o in e.AddedItems) if (o is HitVm v) v.Selected = true;
        UpdateOpenButton();
        ScrollToSelection();
        // A preview that opened by itself belongs to the row it opened for.
        if (_pvAuto) { _pvAuto = false; _previewOn = false; }
        UpdatePreview();
        UpdateHighlight();
        StartAutoPreview();
    }

    // ---- automatic preview: a file that rests on the selection for a second shows itself

    private const int AutoPreviewMs = 1000;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _autoPv;
    private bool _pvAuto;   // the preview is on because of the timer, not Tab

    private static bool WarrantsPreview(HitVm? vm)
    {
        if (vm is not { RawKind: "File", Path: { } p } || vm.Answer != null) return false;
        string ext = System.IO.Path.GetExtension(p);
        return ImageExt.Contains(ext) || TextExt.Contains(ext) || ModelExt.Contains(ext)
            || ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".docx", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>(Re)start the wait for the selected row; does nothing once a preview is already on.</summary>
    private void StartAutoPreview()
    {
        _autoPv?.Stop();
        if (_previewOn || !_visible || _view != View.Results) return;
        int i = ResultsList.SelectedIndex;
        if (i < 0 || i >= Items.Count || !WarrantsPreview(Items[i])) return;
        if (_autoPv == null)
        {
            _autoPv = DispatcherQueue.CreateTimer();
            _autoPv.Interval = TimeSpan.FromMilliseconds(AutoPreviewMs);
            _autoPv.IsRepeating = false;
            _autoPv.Tick += (_, _) =>
            {
                int j = ResultsList.SelectedIndex;
                if (_previewOn || !_visible || _view != View.Results
                    || j < 0 || j >= Items.Count || !WarrantsPreview(Items[j])) return;
                _previewOn = true;
                _pvAuto = true;
                UpdatePreview();
            };
        }
        _autoPv.Start();
    }

    private void OnPreviewDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        Launch(ResultsList.SelectedIndex, reveal: false);

    /// <summary>Forget the preview instantly (the palette is about to open afresh).</summary>
    private void ResetPreview()
    {
        _pvVersion++;
        _previewOn = false;
        _pvAuto = false;
        _autoPv?.Stop();
        _pvPath = null;
        _pvU = _pvTarget = 0;
        _pvBase = _pvCarry = 0;
        _wClock.Reset();
        _panelDip = _wFrom = _wTo = WidthDip;   // opens at its normal width
        foreach (var c in _pvClosing) c.vm.PreviewHeight = 0;
        _pvClosing.Clear();
        Detach();
        ClearPreview();
    }

    /// <summary>Tab: turn file previews on or off for the selected row and those after it.</summary>
    private void TogglePreview()
    {
        _previewOn = !_previewOn;
        _pvAuto = false;   // Tab's choice sticks
        _autoPv?.Stop();
        UpdatePreview();
    }

    /// <summary>Show, switch or hide the preview to match the selected row.</summary>
    private void UpdatePreview()
    {
        int i = ResultsList.SelectedIndex;
        HitVm? vm = _visible && i >= 0 && i < Items.Count ? Items[i] : null;
        // Files show themselves once Tab asks; a sum or a web answer always shows its card.
        string? key = vm switch
        {
            { Answer: { } a } => $"answer:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(vm)}:{System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(a)}",
            { IsCalc: true } => $"calc:{vm.Title}={vm.CopyText}",
            _ => _previewOn ? vm?.Path : null,
        };
        if (key != null && vm != _pvVm) Attach(vm!);
        if (key == _pvPath)
        {
            if (key != null) SetPreviewOpen(true);
            return;
        }

        int v = ++_pvVersion;
        _pvPath = key;
        if (key == null)
        {
            _ = ClosePreviewSoon(v);
            return;
        }
        if (vm!.Answer != null) ShowWebAnswer(vm);
        else if (vm.IsCalc) ShowAnswer(vm);
        else _ = LoadPreview(vm.Path!, vm, v);
        SetPreviewOpen(true);
    }

    /// <summary>Move the shared pane into <paramref name="vm"/>'s row.</summary>
    private void Attach(HitVm vm)
    {
        var old = _pvVm;
        double h = old?.PreviewHeight ?? 0;      // the room its row is laid out with
        double shown = Math.Min(_pvShownDip, h); // the part of it on screen
        Detach();
        if (AnimationsOn && _visible)
        {
            // Back to a row whose room is still closing: open it again from there.
            int j = _pvClosing.FindIndex(c => c.vm == vm);
            double reopen = 0;
            if (j >= 0)
            {
                reopen = _pvClosing[j].u;
                _pvClosing.RemoveAt(j);
            }
            if (old != null && h > 0.5 && !Items.Contains(old))
            {
                // Its row is gone: nothing is left to close there, and the card taking its place
                // starts with its room (StartFromListRoom), so the panel does not dip in between.
                _pvCarry = shown;
                old.PreviewHeight = 0;
                _pvU = reopen;
            }
            else if (old != null && h > 0.5)
            {
                // The pane moves on; the room it had closes as it unrolls in the new row, rather
                // than both jumping.
                // Its row keeps the room laid out, closing on screen from what was shown.
                old.PreviewHeight = h;
                _pvClosing.Add((old, h, EaseInOutInverse(shown / h)));
                _pvU = reopen;
                EnsureFrames();
            }
        }
        _pvVm = vm;
        vm.PreviewContent = PreviewPane;
        if (!_animating) ApplyShape();
    }

    private static double EaseInOut(double u) => u < 0.5 ? 4 * u * u * u : 1 - Math.Pow(-2 * u + 2, 3) / 2;

    /// <summary>One frame of the rows closing behind the pane; true while any still is.</summary>
    private bool StepClosingPreviews(double dt)
    {
        for (int j = _pvClosing.Count - 1; j >= 0; j--)
        {
            var (vm, from, u) = _pvClosing[j];
            u = Math.Max(0, u - dt / PreviewMs);
            if (u == 0)
            {
                vm.PreviewHeight = 0;   // the room it kept while closing on screen (ListMotion.cs)
                _pvClosing.RemoveAt(j);
                _pvCollapsed = true;
            }
            else
            {
                _pvClosing[j] = (vm, from, u);
            }
        }
        return _pvClosing.Count > 0;
    }

    /// <summary>Room still taken by closing rows above row <paramref name="index"/>.</summary>
    private double ClosingAbove(int index)
    {
        double sum = 0;
        foreach (var c in _pvClosing)
        {
            int j = Items.IndexOf(c.vm);
            if (j >= 0 && j < index) sum += c.vm.PreviewHeight;
        }
        return sum;
    }

    /// <summary>Take the pane out of whichever row (or the parking spot) holds it.</summary>
    private void Detach()
    {
        if (_pvVm != null)
        {
            _pvVm.PreviewContent = null;
            _pvVm.PreviewHeight = 0;
            _pvVm = null;
        }
        // A row that was removed from the list may still hold it.
        switch (VisualTreeHelper.GetParent(PreviewPane))
        {
            case ContentPresenter cp: cp.Content = null; break;
            case Panel p: p.Children.Remove(PreviewPane); break;
        }
    }

    /// <summary>
    /// Close after a short pause: while typing, the top result often flips between a file and an
    /// app for a keystroke or two, and the panel should not pulse along.
    /// </summary>
    private async Task ClosePreviewSoon(int v)
    {
        await Task.Delay(140);
        if (v == _pvVersion) SetPreviewOpen(false);
    }

    private void ClearPreview()
    {
        StopModelView();
        _pvFull = FilePaneDip;
        FilePreview.Visibility = Visibility.Visible;
        AnswerPreview.Visibility = Visibility.Collapsed;
        WeatherPreview.Visibility = Visibility.Collapsed;
        StockPreview.Visibility = Visibility.Collapsed;
        SystemPreview.Visibility = Visibility.Collapsed;
        SystemStats.Stop();
        CalendarPreview.Visibility = Visibility.Collapsed;
        FactPreview.Visibility = Visibility.Collapsed;
        AskPreview.Visibility = Visibility.Collapsed;
        ChatPreview.Visibility = Visibility.Collapsed;
        FactImage.Source = null;
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewIcon.Source = null;
        PreviewIcon.Visibility = Visibility.Collapsed;
        PreviewPage.Visibility = Visibility.Collapsed;
        PreviewText.Text = "";
        PreviewNote.Text = "";
        PreviewLocation.Text = "";
        PreviewSize.Text = "";
    }

    private void ShowAnswer(HitVm vm)
    {
        ClearPreview();
        _pvFull = AnswerPaneDip;
        FilePreview.Visibility = Visibility.Collapsed;
        AnswerPreview.Visibility = Visibility.Visible;
        AnswerExpr.Text = PrettySum(vm.Title) + " =";
        AnswerValue.Text = vm.CopyText ?? "";
    }

    private enum PvKind { Missing, Cloud, Text, Image, Pdf, Other }

    private sealed record Probe(PvKind Kind, long Size, DateTime Modified, string? Text);

    private async Task LoadPreview(string path, HitVm vm, int v)
    {
        ClearPreview();
        ShowIcon(vm.Icon);

        // Let a burst of arrow presses settle before touching the disk.
        await Task.Delay(40);
        if (v != _pvVersion) return;

        Probe info;
        try { info = await Task.Run(() => ProbeFile(path)); }
        catch { info = new Probe(PvKind.Missing, 0, default, null); }
        if (v != _pvVersion) return;

        PreviewLocation.Text = System.IO.Path.GetDirectoryName(path) ?? "";
        PreviewSize.Text = info.Kind == PvKind.Missing ? "Not found" : Size(info.Size);
        try
        {
            if (ModelExt.Contains(System.IO.Path.GetExtension(path)) && info.Kind is not (PvKind.Missing or PvKind.Cloud))
            {
                // The viewer lays itself over the stage; the shell thumbnail is what shows meanwhile.
                StartModelView(path);
                await ShowThumbnail(path, v);
                return;
            }
            switch (info.Kind)
            {
                case PvKind.Missing:
                    PreviewNote.Text = "This file no longer exists.";
                    break;
                case PvKind.Cloud:
                    PreviewNote.Text = "Online-only file. Open it to download.";
                    break;
                case PvKind.Text when string.IsNullOrWhiteSpace(info.Text):
                    PreviewNote.Text = "Empty";
                    break;
                case PvKind.Text:
                    // A page, as Windows shows documents: prose in the UI font, code in a mono one.
                    bool prose = ProseExt.Contains(System.IO.Path.GetExtension(path));
                    PreviewText.Text = info.Text;
                    PreviewText.TextWrapping = prose ? TextWrapping.Wrap : TextWrapping.NoWrap;
                    PreviewText.FontFamily = new FontFamily(prose ? "Segoe UI Variable Text" : "Cascadia Mono, Consolas");
                    PreviewText.FontSize = prose ? 13 : 11.5;
                    PreviewPage.Visibility = Visibility.Visible;
                    PreviewIcon.Visibility = Visibility.Collapsed;
                    break;
                case PvKind.Image:
                    if (!await ShowImage(path, v)) await ShowThumbnail(path, v);
                    break;
                case PvKind.Pdf:
                    if (!await ShowPdf(path, v)) await ShowThumbnail(path, v);
                    break;
                default:
                    await ShowThumbnail(path, v);
                    break;
            }
        }
        catch
        {
            // Keep the icon; a preview is best effort.
        }
    }

    private void ShowIcon(ImageSource? icon)
    {
        PreviewIcon.Source = icon;
        PreviewIcon.Width = PreviewIcon.Height = 72;
        PreviewIcon.Visibility = icon != null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPicture(ImageSource src)
    {
        PreviewImage.Source = src;
        PreviewImage.Visibility = Visibility.Visible;
        PreviewIcon.Visibility = Visibility.Collapsed;
    }

    private int PreviewPx => (int)(452 * Scale);   // the preview box's width

    private async Task<bool> ShowImage(string path, int v)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var stream = await file.OpenReadAsync();
            if (v != _pvVersion) return true;
            var bmp = new BitmapImage { DecodePixelWidth = PreviewPx };
            await bmp.SetSourceAsync(stream);
            if (v != _pvVersion) return true;
            ShowPicture(bmp);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> ShowPdf(string path, int v)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            var doc = await PdfDocument.LoadFromFileAsync(file);
            if (v != _pvVersion || doc.PageCount == 0) return v != _pvVersion;
            using var page = doc.GetPage(0);
            using var ms = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(ms, new PdfPageRenderOptions { DestinationWidth = (uint)PreviewPx });
            if (v != _pvVersion) return true;
            ms.Seek(0);
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(ms);
            if (v != _pvVersion) return true;
            ShowPicture(bmp);
            if (doc.PageCount > 1) PreviewSize.Text += $"  ·  {doc.PageCount} pages";
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The shell's own preview image (Office documents, videos, ...), or its large icon.</summary>
    private async Task ShowThumbnail(string path, int v)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(path);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.SingleItem, (uint)Math.Min(PreviewPx, 1024), ThumbnailOptions.ResizeThumbnail);
            if (v != _pvVersion || thumb == null) return;
            var bmp = new BitmapImage();
            await bmp.SetSourceAsync(thumb);
            if (v != _pvVersion) return;
            if (thumb.Type == ThumbnailType.Image)
            {
                ShowPicture(bmp);
            }
            else
            {
                // Only an icon: a crisp large one instead of the scaled-up list icon.
                PreviewIcon.Source = bmp;
                PreviewIcon.Width = PreviewIcon.Height = 96;
                PreviewIcon.Visibility = Visibility.Visible;
            }
        }
        catch
        {
        }
    }

    // ------------------------------------------------------------- off the UI thread

    private static Probe ProbeFile(string path)
    {
        var fi = new FileInfo(path);
        if (!fi.Exists) return new Probe(PvKind.Missing, 0, default, null);
        long size = fi.Length;
        DateTime mod = fi.LastWriteTime;

        // Placeholders of OneDrive, iCloud and the like: reading them would download the file.
        const System.IO.FileAttributes RecallOnOpen = (System.IO.FileAttributes)0x40000, RecallOnDataAccess = (System.IO.FileAttributes)0x400000;
        if ((fi.Attributes & (System.IO.FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess)) != 0)
            return new Probe(PvKind.Cloud, size, mod, null);

        string ext = fi.Extension;
        if (ImageExt.Contains(ext))
            return new Probe(size <= MaxImageBytes ? PvKind.Image : PvKind.Other, size, mod, null);
        if (ext.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            return new Probe(PvKind.Pdf, size, mod, null);
        if (ext.Equals(".docx", StringComparison.OrdinalIgnoreCase))
        {
            string? doc = DocxText(path);
            return doc != null ? new Probe(PvKind.Text, size, mod, doc) : new Probe(PvKind.Other, size, mod, null);
        }
        // Known text types, plus small files of any other type that turn out to be text.
        if (TextExt.Contains(ext) || size < (1 << 20))
        {
            string? text = ReadText(path, known: TextExt.Contains(ext));
            if (text != null) return new Probe(PvKind.Text, size, mod, text);
        }
        return new Probe(PvKind.Other, size, mod, null);
    }

    /// <summary>The start of a text file, or null if it looks binary.</summary>
    private static string? ReadText(string path, bool known)
    {
        byte[] buf = new byte[MaxTextBytes];
        int n;
        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan))
        {
            n = 0;
            int r;
            while (n < buf.Length && (r = fs.Read(buf, n, buf.Length - n)) > 0) n += r;
        }

        Encoding enc = new UTF8Encoding(false);
        int skip = 0;
        if (n >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) { enc = Encoding.Unicode; skip = 2; }
        else if (n >= 2 && buf[0] == 0xFE && buf[1] == 0xFF) { enc = Encoding.BigEndianUnicode; skip = 2; }
        else if (n >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) skip = 3;

        if (skip != 2)
        {
            // Binary sniff: NUL bytes or many control characters mean it is not text.
            int bad = 0;
            for (int i = skip; i < Math.Min(n, 4096); i++)
            {
                byte b = buf[i];
                if (b == 0) return null;
                if (b < 0x09 || (b > 0x0D && b < 0x20 && b != 0x1B)) bad++;
            }
            if (!known && bad > 8) return null;
        }

        string s = enc.GetString(buf, skip, n - skip);
        if (n == buf.Length && s.Length > 0 && s[^1] == '�') s = s[..^1];   // cut mid-character
        return Tidy(s);
    }

    /// <summary>Text of a Word document's body, paragraph by paragraph.</summary>
    private static string? DocxText(string path)
    {
        try
        {
            using var zip = ZipFile.OpenRead(path);
            var entry = zip.GetEntry("word/document.xml");
            if (entry == null) return null;
            using var xr = XmlReader.Create(entry.Open(), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
            var sb = new StringBuilder();
            const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            while (xr.Read() && sb.Length < 12_000)
            {
                if (xr.NodeType == XmlNodeType.Element && xr.NamespaceURI == W)
                {
                    switch (xr.LocalName)
                    {
                        case "t": sb.Append(xr.ReadElementContentAsString()); break;
                        case "tab": sb.Append('\t'); break;
                        case "br": sb.Append('\n'); break;
                    }
                }
                else if (xr.NodeType == XmlNodeType.EndElement && xr.LocalName == "p" && xr.NamespaceURI == W)
                {
                    sb.Append('\n');
                }
            }
            return Tidy(sb.ToString());
        }
        catch
        {
            return null;
        }
    }

    private static string Tidy(string s)
    {
        s = s.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\t", "    ");
        // Collapse runs of blank lines and stop after a screenful or so.
        var sb = new StringBuilder(Math.Min(s.Length, MaxTextBytes));
        int lines = 0, blank = 0;
        foreach (var line in s.Split('\n'))
        {
            if (string.IsNullOrWhiteSpace(line)) { if (++blank > 1) continue; }
            else blank = 0;
            if (lines++ >= MaxTextLines) break;
            sb.Append(line.Length > 400 ? line[..400] : line).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    private static string Size(long b) => b switch
    {
        < 1024 => $"{b} bytes",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024):0.#} MB",
        _ => $"{b / (1024.0 * 1024 * 1024):0.##} GB",
    };
}
