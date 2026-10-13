using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

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
    private static extern unsafe int pc_icon(ulong gen, int index, [MarshalAs(UnmanagedType.LPUTF8Str)] string? key, byte* buf);

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
        [property: JsonPropertyName("pinned")] bool Pinned = false);

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
        [property: JsonPropertyName("replace")] bool Replace = false,
        [property: JsonPropertyName("prepend")] bool Prepend = false);

    /// <summary>Blocks (tens of ms; seconds if the model process has to start): call off the UI thread.</summary>
    public static Appended Semantic(ulong gen, string query)
    {
        IntPtr p = pc_semantic(gen, query);
        try
        {
            string json = Marshal.PtrToStringUTF8(p) ?? "{}";
            return JsonSerializer.Deserialize<Appended>(json) ?? new Appended(gen, 0, new());
        }
        finally
        {
            pc_free(p);
        }
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

    public static List<ActionRow> Actions(ulong gen, int index)
    {
        IntPtr p = pc_actions(gen, index);
        try
        {
            return JsonSerializer.Deserialize<List<ActionRow>>(Marshal.PtrToStringUTF8(p) ?? "[]") ?? new();
        }
        finally
        {
            pc_free(p);
        }
    }

    public enum ActResult { NotApplicable = -1, Hide = 1, Refresh = 3 }

    public static ActResult Act(ulong gen, int index, string id) => (ActResult)pc_act(gen, index, id);

    private static SearchResult ReadResult(IntPtr p)
    {
        try
        {
            string json = Marshal.PtrToStringUTF8(p) ?? "{}";
            return JsonSerializer.Deserialize<SearchResult>(json) ?? new SearchResult(0, new());
        }
        finally
        {
            pc_free(p);
        }
    }

    public enum LaunchResult { Nothing = 0, Hide = 1, Quit = 2, AllApps = 4 }

    public static LaunchResult Launch(ulong gen, int index, bool reveal) =>
        (LaunchResult)pc_launch(gen, index, reveal ? 1 : 0);

    public static bool AutostartEnabled() => pc_autostart_get() != 0;
    public static bool SetAutostart(bool enable) => pc_autostart_set(enable ? 1 : 0) != 0;
    public static void Reindex() => pc_reindex();

    /// <summary>Premultiplied RGBA, IconSize x IconSize, or null.</summary>
    public static unsafe byte[]? Icon(ulong gen, int index, string? key)
    {
        var buf = new byte[IconSize * IconSize * 4];
        fixed (byte* b = buf)
        {
            return pc_icon(gen, index, key, b) != 0 ? buf : null;
        }
    }
}
