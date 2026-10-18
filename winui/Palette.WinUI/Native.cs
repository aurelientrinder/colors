using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace Palette;

/// <summary>P/Invoke surface of palette_core.dll (the Rust engine).</summary>
public static class Native
{
    private const string Dll = "palette_core.dll";

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pc_init();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pc_search([MarshalAs(UnmanagedType.LPUTF8Str)] string query);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pc_semantic(ulong gen, [MarshalAs(UnmanagedType.LPUTF8Str)] string query);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pc_semantic_status();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pc_read_window(nint hwnd);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pc_clip_add([MarshalAs(UnmanagedType.LPUTF8Str)] string text, long at);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pc_clip_clear();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pc_rename(ulong gen, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pc_destinations(ulong gen, int index);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pc_file_outcome();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pc_file_undo();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pc_selection(int waitMs);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pc_passage([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string query);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pc_plan(ulong gen, [MarshalAs(UnmanagedType.LPUTF8Str)] string query);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pc_launch(ulong gen, int index, int reveal);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pc_all_apps();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pc_actions(ulong gen, int index);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pc_act(ulong gen, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pc_autostart_get();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pc_autostart_set(int enable);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pc_reindex();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int pc_icon(ulong gen, int index, byte* buf);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern unsafe int pc_icon_key([MarshalAs(UnmanagedType.LPUTF8Str)] string key, byte* buf);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pc_icon_keys();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pc_icon_size();

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern void pc_free(IntPtr p);

    public static int IconSize { get; private set; } = 48;

    public static void Init()
    {
        pc_init();
        IconSize = pc_icon_size();
    }

    public sealed record SearchResult(
        [property: JsonPropertyName("gen")] ulong Gen,
        [property: JsonPropertyName("hits")] List<Hit> Hits);

    public sealed record Hit(
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("subtitle")] string Subtitle,
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("icon_key")] string? IconKey,
        [property: JsonPropertyName("copy_text")] string? CopyText,
        [property: JsonPropertyName("related")] bool Related = false,
        [property: JsonPropertyName("path")] string? Path = null,
        [property: JsonPropertyName("pinned")] bool Pinned = false,
        [property: JsonPropertyName("folder")] string? Folder = null,
        [property: JsonPropertyName("control")] Control? Control = null,
        [property: JsonPropertyName("does")] string? Does = null);

    /// <summary>
    /// What a row draws to change its setting in place: Kind "switch" (On), "slider" (Value 0..100;
    /// On means muted for the volume) or "media". Key names the setting for <see cref="Quick"/>.
    /// </summary>
    public sealed record Control(
        [property: JsonPropertyName("kind")] string Kind,
        [property: JsonPropertyName("key")] string Key,
        [property: JsonPropertyName("on")] bool On,
        [property: JsonPropertyName("value")] int Value);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern int pc_quick([MarshalAs(UnmanagedType.LPUTF8Str)] string id);

    [DllImport(Dll, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pc_quick_control([MarshalAs(UnmanagedType.LPUTF8Str)] string key);

    /// <summary>Change a quick setting: "volume:40", "wifi:on". Blocks briefly (radios, WMI): call off the UI thread.</summary>
    public static bool Quick(string id) => pc_quick(id) != 0;

    /// <summary>The control for <paramref name="key"/> as the setting is now, or null.</summary>
    public static Control? QuickControl(string key) => Read(pc_quick_control(key), NativeJson.Default.Control);

    /// <summary>One entry of a result's action list; <c>Copy</c> is set for copy actions.</summary>
    public sealed record ActionRow(
        [property: JsonPropertyName("id")] string Id,
        [property: JsonPropertyName("title")] string Title,
        [property: JsonPropertyName("hint")] string Hint,
        [property: JsonPropertyName("copy")] string? Copy);

    /// <summary>Semantic rows to append after the instant results; Base is the index of the first.</summary>
    public sealed record Appended(
        [property: JsonPropertyName("gen")] ulong Gen,
        [property: JsonPropertyName("base")] int Base,
        [property: JsonPropertyName("hits")] List<Hit> Hits,
        [property: JsonPropertyName("replace")] bool Replace = false);

    /// <summary>Blocks (tens of ms; seconds if the model process has to start): call off the UI thread.</summary>
    public static Appended Semantic(ulong gen, string query) =>
        Read(pc_semantic(gen, query), NativeJson.Default.Appended) ?? new Appended(gen, 0, new());

    /// <summary>
    /// The language model's plan for a request the rules did not read, as the whole new list
    /// (the plan first). About 60 ms; seconds if its process has to start: call off the UI thread.
    /// </summary>
    public static Appended Plan(ulong gen, string query) =>
        Read(pc_plan(gen, query), NativeJson.Default.Appended) ?? new Appended(gen, 0, new());

    /// <summary>The part of a document that answers a query; Marks are (start, length) pairs of the matching words.</summary>
    public sealed record PassageReply(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("marks")] List<int[]> Marks);

    /// <summary>
    /// The passage of <paramref name="path"/> that <paramref name="query"/> matched, or null. Reading a
    /// PDF the helper has not read lately takes a moment: call off the UI thread.
    /// </summary>
    public static PassageReply? Passage(string path, string query) => Read(pc_passage(path, query), NativeJson.Default.PassageReply);

    /// <summary>
    /// Start reading the window that had the focus when the palette opened (IntPtr.Zero: none): its
    /// selected text and its commands. Returns at once.
    /// </summary>
    public static void ReadWindow(IntPtr hwnd) => pc_read_window(hwnd);

    /// <summary>Something was copied at that unix time; the engine keeps it in memory for "clip" searches.</summary>
    public static void ClipAdd(string text, long unixSeconds) => pc_clip_add(text, unixSeconds);

    /// <summary>Forget everything copied.</summary>
    public static void ClipClear() => pc_clip_clear();

    /// <summary>
    /// The text selected in that window, on one line, waiting up to <paramref name="waitMs"/> for the
    /// read to finish; null when nothing is selected. Blocks while it waits: call off the UI thread
    /// unless the wait is 0.
    /// </summary>
    public static string? Selection(int waitMs)
    {
        IntPtr p = pc_selection(waitMs);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUTF8(p); }
        finally { pc_free(p); }
    }

    public static string SemanticStatus()
    {
        IntPtr p = pc_semantic_status();
        try
        {
            return Marshal.PtrToStringUTF8(p) ?? "";
        }
        finally
        {
            pc_free(p);
        }
    }

    public static SearchResult Search(string query) => ReadResult(pc_search(query));

    /// <summary>Every app, A to Z, as a new generation.</summary>
    public static SearchResult AllApps() => ReadResult(pc_all_apps());

    public static List<ActionRow> Actions(ulong gen, int index) =>
        Read(pc_actions(gen, index), NativeJson.Default.ListActionRow) ?? new();

    public enum ActResult { NotApplicable = -1, Hide = 1, Refresh = 3, Done = 5 }

    /// <summary>How the last file operation (zip, rename, move, recycle) went.</summary>
    public sealed record FileOutcome(
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("ok")] bool Ok,
        [property: JsonPropertyName("undo")] bool Undo);

    public static FileOutcome Outcome() => Read(pc_file_outcome(), NativeJson.Default.FileOutcome) ?? new("", false, false);

    public static void UndoFile() => pc_file_undo();

    /// <summary>Rename the file or folder of a row; the result is in <see cref="Outcome"/>.</summary>
    public static ActResult Rename(ulong gen, int index, string name) => (ActResult)pc_rename(gen, index, name);

    /// <summary>Folders the row could be moved to, as action rows.</summary>
    public static List<ActionRow> Destinations(ulong gen, int index) =>
        Read(pc_destinations(gen, index), NativeJson.Default.ListActionRow) ?? new();

    public static ActResult Act(ulong gen, int index, string id) => (ActResult)pc_act(gen, index, id);

    private static SearchResult ReadResult(IntPtr p) =>
        Read(p, NativeJson.Default.SearchResult) ?? new SearchResult(0, new());

    /// <summary>
    /// Parse the engine's JSON reply in place, as the UTF-8 it is (no string copy, no reflection:
    /// the parser is generated at build time), then free it. Null for a null or malformed reply.
    /// </summary>
    private static unsafe T? Read<T>(IntPtr p, JsonTypeInfo<T> type) where T : class
    {
        if (p == IntPtr.Zero) return null;
        try
        {
            return JsonSerializer.Deserialize(MemoryMarshal.CreateReadOnlySpanFromNullTerminated((byte*)p), type);
        }
        catch (JsonException)
        {
            return null;
        }
        finally
        {
            pc_free(p);
        }
    }

    public enum LaunchResult { Nothing = 0, Hide = 1, Quit = 2, AllApps = 4, UseSelection = 5 }

    public static LaunchResult Launch(ulong gen, int index, bool reveal) =>
        (LaunchResult)pc_launch(gen, index, reveal ? 1 : 0);

    public static bool AutostartEnabled() => pc_autostart_get() != 0;
    public static bool SetAutostart(bool enable) => pc_autostart_set(enable ? 1 : 0) != 0;
    public static void Reindex() => pc_reindex();

    /// <summary>Premultiplied BGRA, IconSize x IconSize, or null.</summary>
    public static unsafe byte[]? Icon(ulong gen, int index)
    {
        var buf = new byte[IconSize * IconSize * 4];
        fixed (byte* b = buf)
        {
            return pc_icon(gen, index, b) != 0 ? buf : null;
        }
    }

    /// <summary>The icon a hit's <see cref="Hit.IconKey"/> stands for, from any generation, or null.</summary>
    public static unsafe byte[]? Icon(string key)
    {
        var buf = new byte[IconSize * IconSize * 4];
        fixed (byte* b = buf)
        {
            return pc_icon_key(key, b) != 0 ? buf : null;
        }
    }

    /// <summary>The icon keys of every app, setting and folder, for warming the cache.</summary>
    public static List<string> IconKeys() => Read(pc_icon_keys(), NativeJson.Default.ListString) ?? new();
}

/// <summary>Parsers for the engine's replies, generated at build time.</summary>
[JsonSerializable(typeof(Native.SearchResult))]
[JsonSerializable(typeof(Native.Appended))]
[JsonSerializable(typeof(List<Native.ActionRow>))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(Native.Control))]
[JsonSerializable(typeof(Native.FileOutcome))]
[JsonSerializable(typeof(Native.PassageReply))]
internal sealed partial class NativeJson : JsonSerializerContext
{
}
