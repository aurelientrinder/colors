using System.Globalization;
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
/// Preview of the selected result, shown automatically when it is a file: images and the first
/// page of PDFs are rendered, text, code and Word documents show their opening text, anything
/// else gets the shell's thumbnail. Loading is asynchronous and dropped as soon as the selection
/// moves on.
/// </summary>
public sealed partial class MainWindow
{
    private string? _pvPath;      // file the preview shows (or is loading)
    private int _pvVersion;       // bumped on every change; stale loads check it and stop

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

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => UpdatePreview();

    private void OnPreviewDoubleTapped(object sender, DoubleTappedRoutedEventArgs e) =>
        Launch(ResultsList.SelectedIndex, reveal: false);

    /// <summary>Forget the preview instantly (the palette is about to open afresh).</summary>
    private void ResetPreview()
    {
        _pvVersion++;
        _pvPath = null;
        _pvU = _pvTarget = 0;
        ClearPreview();
    }

    /// <summary>Show, switch or hide the preview to match the selected row.</summary>
    private void UpdatePreview()
    {
        int i = ResultsList.SelectedIndex;
        HitVm? vm = _visible && i >= 0 && i < Items.Count ? Items[i] : null;
        string? path = _sideDip > 0 ? vm?.Path : null;
        if (path == _pvPath)
        {
            if (path != null) SetPreviewOpen(true);
            return;
        }

        int v = ++_pvVersion;
        _pvPath = path;
        if (path == null)
        {
            _ = ClosePreviewSoon(v);
            return;
        }
        SetPreviewOpen(true);
        _ = LoadPreview(path, vm!, v);
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
        PreviewImage.Source = null;
        PreviewImage.Visibility = Visibility.Collapsed;
        PreviewIcon.Source = null;
        PreviewIcon.Visibility = Visibility.Collapsed;
        PreviewScroll.Visibility = Visibility.Collapsed;
        PreviewText.Text = "";
        PreviewNote.Text = "";
        PreviewName.Text = PreviewMeta.Text = PreviewFolder.Text = "";
    }

    private enum PvKind { Missing, Cloud, Text, Image, Pdf, Other }

    private sealed record Probe(PvKind Kind, long Size, DateTime Modified, string? Text);

    private async Task LoadPreview(string path, HitVm vm, int v)
    {
        ClearPreview();
        PreviewName.Text = System.IO.Path.GetFileName(path);
        PreviewFolder.Text = System.IO.Path.GetDirectoryName(path) ?? "";
        ShowIcon(vm.Icon);

        // Let a burst of arrow presses settle before touching the disk.
        await Task.Delay(40);
        if (v != _pvVersion) return;

        Probe info;
        try { info = await Task.Run(() => ProbeFile(path)); }
        catch { info = new Probe(PvKind.Missing, 0, default, null); }
        if (v != _pvVersion) return;

        PreviewMeta.Text = info.Kind == PvKind.Missing ? "Not found" : Describe(path, info);
        try
        {
            switch (info.Kind)
            {
                case PvKind.Missing:
                    PreviewNote.Text = "This file no longer exists.";
                    break;
                case PvKind.Cloud:
                    PreviewNote.Text = "Online-only file. Open it to download.";
                    break;
                case PvKind.Text:
                    PreviewText.Text = info.Text ?? "";
                    PreviewText.TextWrapping = ProseExt.Contains(System.IO.Path.GetExtension(path)) ? TextWrapping.Wrap : TextWrapping.NoWrap;
                    PreviewScroll.ChangeView(0, 0, null, true);
                    PreviewScroll.Visibility = Visibility.Visible;
                    PreviewIcon.Visibility = Visibility.Collapsed;
                    if (string.IsNullOrWhiteSpace(info.Text)) { PreviewScroll.Visibility = Visibility.Collapsed; PreviewIcon.Visibility = Visibility.Visible; PreviewNote.Text = "Empty"; }
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

    private int PreviewPx => (int)(2 * _sideDip * Scale);

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
            if (doc.PageCount > 1) PreviewMeta.Text += $"  ·  {doc.PageCount} pages";
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

    private static string Describe(string path, Probe p)
    {
        string ext = System.IO.Path.GetExtension(path).TrimStart('.').ToUpperInvariant();
        string kind = ext.Length > 0 ? ext + " file" : "File";
        return $"{kind}  ·  {Size(p.Size)}  ·  Modified {When(p.Modified)}";
    }

    private static string Size(long b) => b switch
    {
        < 1024 => $"{b} bytes",
        < 1024 * 1024 => $"{b / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{b / (1024.0 * 1024):0.#} MB",
        _ => $"{b / (1024.0 * 1024 * 1024):0.##} GB",
    };

    private static string When(DateTime t)
    {
        var c = CultureInfo.CurrentCulture;
        string time = t.ToString("t", c);
        var days = (DateTime.Today - t.Date).Days;
        return days switch
        {
            0 => $"today, {time}",
            1 => $"yesterday, {time}",
            < 7 and > 1 => $"{t.ToString("dddd", c)}, {time}",
            _ => t.ToString(t.Year == DateTime.Today.Year ? "d MMM" : "d MMM yyyy", c),
        };
    }
}
