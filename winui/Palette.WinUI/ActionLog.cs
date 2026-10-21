using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Palette;

/// <summary>A file or folder moved from one place to another (a rename is a move within its folder).</summary>
public sealed record FileMove(string From, string To);

/// <summary>
/// Something the chat did on the PC, with what it takes to undo it: files moved (put back), files
/// recycled (restored from the Recycle Bin), a folder made (removed while empty), a setting changed
/// (set back to <see cref="Before"/>, a quick setting's "key:value"), a reminder set (cancelled).
/// </summary>
public sealed record ActionRecord(
    string Id, string Kind, string Summary, DateTime At, string? ChatId,
    List<FileMove>? Moves = null, List<string>? Recycled = null, string? MadeFolder = null,
    string? Before = null, string? ReminderId = null, bool Undone = false)
{
    public bool CanUndo => !Undone && Kind != "open";
}

/// <summary>
/// What the chat has done on the PC, newest last, kept in %LOCALAPPDATA%\palette\actions.json (the last
/// <see cref="Keep"/>), so each can be undone from its line in the chat, or by asking ("undo that"),
/// even after a restart. Undoing checks first that nothing has taken the place since.
/// </summary>
internal static class ActionLog
{
    private const int Keep = 200;
    private static readonly object Gate = new();
    private static List<ActionRecord>? _all;

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "palette", "actions.json");

    private static List<ActionRecord> All()
    {
        if (_all != null) return _all;
        try
        {
            if (File.Exists(FilePath)) _all = JsonSerializer.Deserialize(File.ReadAllText(FilePath), ActionJson.Default.ListActionRecord);
        }
        catch (Exception ex)
        {
            App.Log("actions not loaded: " + ex.Message);
        }
        return _all ??= new();
    }

    private static void Save()
    {
        try
        {
            var all = All();
            if (all.Count > Keep) all.RemoveRange(0, all.Count - Keep);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath + ".tmp", JsonSerializer.Serialize(all, ActionJson.Default.ListActionRecord));
            File.Move(FilePath + ".tmp", FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            App.Log("actions not saved: " + ex.Message);
        }
    }

    public static ActionRecord Add(ActionRecord r)
    {
        lock (Gate)
        {
            All().Add(r);
            Save();
        }
        App.Log($"action {r.Kind}: {r.Summary}");
        return r;
    }

    public static string NewId() => Guid.NewGuid().ToString("N")[..12];

    /// <summary>The latest thing that can still be undone (in <paramref name="chatId"/> first, else anywhere), within the last day.</summary>
    public static ActionRecord? LastUndoable(string? chatId)
    {
        lock (Gate)
        {
            var recent = All().Where(r => r.CanUndo && DateTime.Now - r.At < TimeSpan.FromDays(1)).ToList();
            return recent.LastOrDefault(r => r.ChatId == chatId) ?? recent.LastOrDefault();
        }
    }

    /// <summary>The record as it is now (a line in the chat holds the one from when it was done).</summary>
    public static ActionRecord? Get(string id)
    {
        lock (Gate) return All().FirstOrDefault(r => r.Id == id);
    }

    /// <summary>Undo <paramref name="r"/>: whether it worked, and a line saying what happened.</summary>
    public static (bool ok, string message) Undo(ActionRecord r)
    {
        var now = Get(r.Id) ?? r;
        if (now.Undone) return (false, "Already undone");
        (bool ok, string message) result;
        try
        {
            result = now.Kind switch
            {
                "move" or "rename" => PutBack(now),
                "recycle" => Restore(now.Recycled ?? new()),
                "folder" => RemoveFolder(now.MadeFolder),
                "setting" => now.Before != null && Native.Quick(now.Before) ? (true, "Set back as it was") : (false, "Couldn't set it back"),
                "reminder" => now.ReminderId != null && ReminderStore.Remove(now.ReminderId) ? (true, "Reminder cancelled") : (false, "That reminder is gone already"),
                _ => (false, "That can't be undone"),
            };
        }
        catch (Exception ex)
        {
            result = (false, "Couldn't undo it: " + ex.Message);
        }
        if (result.ok)
        {
            lock (Gate)
            {
                int i = All().FindIndex(x => x.Id == r.Id);
                if (i >= 0) All()[i] = All()[i] with { Undone = true };
                Save();
            }
        }
        App.Log($"undo {r.Kind} {r.Summary}: {result.message}");
        return result;
    }

    /// <summary>Move every file back where it was (the last moved first), then remove the folder made for them if it is empty.</summary>
    private static (bool, string) PutBack(ActionRecord r)
    {
        var moves = r.Moves ?? new();
        int back = 0;
        var skipped = new List<string>();
        foreach (var m in Enumerable.Reverse(moves))
        {
            if (!Exists(m.To) || Exists(m.From))
            {
                skipped.Add(Path.GetFileName(m.From));
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(m.From)!);
            MovePath(m.To, m.From);
            back++;
        }
        if (r.MadeFolder != null) RemoveFolder(r.MadeFolder);
        if (back == 0) return (false, "Couldn't put them back: they have moved, or something else is in their place");
        string what = r.Kind == "rename" ? "Put the old name back" : back == 1 ? "Put it back" : $"Put {back} back";
        return (true, skipped.Count == 0 ? what : $"{what} ({skipped.Count} had moved since)");
    }

    private static (bool, string) RemoveFolder(string? folder)
    {
        if (folder == null || !Directory.Exists(folder)) return (true, "The folder is gone already");
        if (Directory.EnumerateFileSystemEntries(folder).Any()) return (false, "The folder has things in it now: left it");
        Directory.Delete(folder);
        return (true, "Removed the folder");
    }

    // ------------------------------------------------------------------ files

    public static bool Exists(string p) => File.Exists(p) || Directory.Exists(p);

    /// <summary>A UNC or device path (\\server\share, //server, \\?\…) rather than one on a drive of this PC.</summary>
    public static bool IsNetwork(string p) => p.Length >= 2 && (p[0] is '\\' or '/') && (p[1] is '\\' or '/');

    /// <summary>Extensions Windows runs rather than opens in an app: programs, scripts, installers, shortcuts.</summary>
    private static readonly HashSet<string> Runnable = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".com", ".scr", ".pif", ".bat", ".cmd", ".ps1", ".psm1", ".vbs", ".vbe", ".js", ".jse", ".wsf", ".wsh",
        ".hta", ".msi", ".msp", ".msix", ".msixbundle", ".appx", ".appxbundle", ".appinstaller", ".msc", ".cpl", ".lnk",
        ".url", ".reg", ".jar", ".py", ".pyw", ".appref-ms", ".application", ".inf", ".scf", ".settingcontent-ms",
    };

    public static bool IsRunnable(string path) => File.Exists(path) && Runnable.Contains(Path.GetExtension(path));

    public static void MovePath(string from, string to)
    {
        if (Directory.Exists(from)) Directory.Move(from, to);
        else File.Move(from, to);
    }

    /// <summary>"dir\name", or "dir\stem (2).ext" and so on when that is taken: nothing is ever overwritten.</summary>
    public static string FreeName(string dir, string name)
    {
        string first = Path.Combine(dir, name);
        if (!Exists(first)) return first;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int n = 2; ; n++)
        {
            string p = Path.Combine(dir, $"{stem} ({n}){ext}");
            if (!Exists(p)) return p;
        }
    }

    /// <summary>
    /// Why the chat may not touch <paramref name="path"/> (a whole drive, Windows, programs, app data, the
    /// Recycle Bin), or null when it may: it acts on the user's own files only.
    /// </summary>
    public static string? Forbidden(string path)
    {
        string full;
        try { full = Path.GetFullPath(path).TrimEnd('\\'); }
        catch (Exception) { return "not a path"; }
        // \\server\share and \\?\ paths: reaching one sends the user's sign-in to that server.
        if (IsNetwork(full)) return "a network location";
        if (full.Length <= 3) return "a whole drive";
        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f).TrimEnd('\\');
        var off = new[]
        {
            F(Environment.SpecialFolder.Windows), F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86),
            F(Environment.SpecialFolder.CommonApplicationData), F(Environment.SpecialFolder.ApplicationData), F(Environment.SpecialFolder.LocalApplicationData),
        };
        foreach (var o in off.Where(o => o.Length > 3))
            if (full.Equals(o, StringComparison.OrdinalIgnoreCase) || full.StartsWith(o + "\\", StringComparison.OrdinalIgnoreCase))
                return "a system or program folder";
        if (full.Contains("\\$Recycle.Bin", StringComparison.OrdinalIgnoreCase)) return "the Recycle Bin";
        if (full.Equals(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
            return "your whole user folder";
        return null;
    }

    // ------------------------------------------------------------------ the Recycle Bin

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEOPSTRUCT
    {
        public IntPtr hwnd;
        public uint wFunc;
        public string pFrom;
        public string? pTo;
        public ushort fFlags;
        public int fAnyOperationsAborted;
        public IntPtr hNameMappings;
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperation(ref SHFILEOPSTRUCT op);

    /// <summary>Put <paramref name="paths"/> in the Recycle Bin, silently. Those that went in.</summary>
    public static List<string> Recycle(List<string> paths)
    {
        const uint FO_DELETE = 3;
        const ushort FOF_SILENT = 0x4, FOF_NOCONFIRMATION = 0x10, FOF_ALLOWUNDO = 0x40, FOF_NOERRORUI = 0x400;
        var done = new List<string>();
        foreach (var p in paths)
        {
            var op = new SHFILEOPSTRUCT
            {
                wFunc = FO_DELETE, pFrom = p + "\0",   // the list ends with an empty string
                fFlags = FOF_SILENT | FOF_NOCONFIRMATION | FOF_ALLOWUNDO | FOF_NOERRORUI,
            };
            if (SHFileOperation(ref op) == 0 && op.fAnyOperationsAborted == 0 && !Exists(p)) done.Add(p);
        }
        return done;
    }

    /// <summary>
    /// Take <paramref name="paths"/> back out of the Recycle Bin, to where they were. Each drive keeps the
    /// user's bin in $Recycle.Bin\&lt;their SID&gt;: per item a $I file (its old path) and a $R one (the item).
    /// </summary>
    private static (bool, string) Restore(List<string> paths)
    {
        string sid = WindowsIdentity.GetCurrent().User?.Value ?? "";
        int back = 0;
        var missing = new List<string>();
        foreach (var original in paths)
        {
            string bin = Path.Combine(Path.GetPathRoot(original) ?? "C:\\", "$Recycle.Bin", sid);
            var found = Directory.Exists(bin)
                ? Directory.EnumerateFiles(bin, "$I*").Select(i => (i, info: ReadInfo(i))).Where(t => t.info.path != null
                    && string.Equals(t.info.path, original, StringComparison.OrdinalIgnoreCase)).OrderByDescending(t => t.info.when).FirstOrDefault()
                : default;
            string? item = found.i == null ? null : Path.Combine(bin, "$R" + Path.GetFileName(found.i)[2..]);
            if (item == null || !Exists(item) || Exists(original))
            {
                missing.Add(Path.GetFileName(original));
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(original)!);
            MovePath(item, original);
            File.Delete(found.i!);
            back++;
        }
        if (back == 0) return (false, "Couldn't restore them: they are no longer in the Recycle Bin");
        string what = back == 1 ? "Restored it from the Recycle Bin" : $"Restored {back} from the Recycle Bin";
        return (true, missing.Count == 0 ? what : $"{what} ({missing.Count} no longer there)");
    }

    /// <summary>A $I file: version 1 (a fixed 260-character path) or 2 (its length, then the path); and when it was deleted.</summary>
    private static (string? path, DateTime when) ReadInfo(string file)
    {
        try
        {
            byte[] b = File.ReadAllBytes(file);
            if (b.Length < 28) return (null, default);
            long version = BitConverter.ToInt64(b, 0);
            var when = DateTime.FromFileTime(BitConverter.ToInt64(b, 16));
            string path = version >= 2
                ? Encoding.Unicode.GetString(b, 28, Math.Min(b.Length - 28, Math.Max(0, BitConverter.ToInt32(b, 24) - 1) * 2))
                : Encoding.Unicode.GetString(b, 24, Math.Min(b.Length - 24, 520));
            return (path.TrimEnd('\0'), when);
        }
        catch (Exception)
        {
            return (null, default);
        }
    }

    // ------------------------------------------------------------------ the usual folders

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    private static string Downloads()
    {
        if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var p) == 0)
        {
            string s = Marshal.PtrToStringUni(p) ?? "";
            Marshal.FreeCoTaskMem(p);
            if (s.Length > 0) return s;
        }
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    /// <summary>The user's usual folder named <paramref name="word"/> (Documents, Téléchargements, photos…), or null.</summary>
    public static string? Known(string word) => word.Trim().ToLowerInvariant() switch
    {
        "documents" or "my documents" or "docs" => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "downloads" or "download" or "téléchargements" => Downloads(),
        "desktop" or "bureau" => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        "pictures" or "photos" or "images" => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        "videos" or "vidéos" => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
        "music" or "musique" => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        _ => null,
    };

    /// <summary>"Documents › Invoices", for people: the folder under the user's home.</summary>
    public static string Show(string folder)
    {
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string f = folder.StartsWith(home, StringComparison.OrdinalIgnoreCase) ? folder[home.Length..].TrimStart('\\') : folder;
        var parts = f.Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 3) parts = new[] { "…" }.Concat(parts[^3..]).ToArray();
        return parts.Length == 0 ? folder : string.Join(" › ", parts);
    }
}

[JsonSerializable(typeof(List<ActionRecord>))]
internal sealed partial class ActionJson : JsonSerializerContext
{
}
