using System.Text.Json;
using Microsoft.Win32;

namespace Palette;

/// <summary>User options, kept in %LOCALAPPDATA%\palette\settings.json.</summary>
internal sealed class Settings
{
    /// <summary>A tap of the Windows key opens the palette instead of Start.</summary>
    public bool WinKeyOpens { get; set; } = true;

    /// <summary>Win+S and Win+Q open the palette, and typing into Start or the taskbar search flyout continues there.</summary>
    public bool ReplaceSearch { get; set; } = true;

    /// <summary>Questions like "weather in Paris" or "MSFT stock" get an answer card from the web (see WebAnswers).</summary>
    public bool WebAnswers { get; set; } = true;

    /// <summary>The chat remembers what you tell it about yourself, across conversations (MemoryStore).</summary>
    public bool ChatMemory { get; set; } = true;

    /// <summary>The line of keys for the selected row, in the search card.</summary>
    public bool KeyHints { get; set; } = true;

    /// <summary>
    /// When the palette opens it reads the window behind it: the text selected there (offered on Home)
    /// and its commands (found by typing). Nothing is kept or sent anywhere (see uia.rs).
    /// </summary>
    public bool ReadWindow { get; set; } = true;

    /// <summary>
    /// Remember what is copied (Windows' clipboard history and copies made while Palette runs), in
    /// memory only, to be found by typing "clip" (see Clipboard.cs).
    /// </summary>
    public bool ClipboardHistory { get; set; } = true;

    /// <summary>
    /// The shortcut that opens Palette: modifiers (MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4, MOD_WIN = 8)
    /// and a virtual key. Key 0 is the default, Alt+Space, with Ctrl+Space and Ctrl+Alt+Space tried when
    /// another program has it.
    /// </summary>
    public uint HotkeyMods { get; set; }
    public uint HotkeyVk { get; set; }

    /// <summary>Find documents and settings by meaning (the helper process and its model). Read at start.</summary>
    public bool SemanticSearch { get; set; } = true;

    /// <summary>Let the meaning index keep working on battery. Read at start.</summary>
    public bool IndexOnBattery { get; set; }

    /// <summary>
    /// Secret iCal addresses of Google calendars (Google Calendar › Settings › Integrate calendar), read by
    /// the calendar card (see Calendar.cs). The address is the key to the calendar: it stays on this PC.
    /// </summary>
    public List<string> CalendarFeeds { get; set; } = new();

    /// <summary>The engine reads these two options from the environment when it starts (before it does).</summary>
    public void ApplyEnvironment()
    {
        if (!SemanticSearch) Environment.SetEnvironmentVariable("PALETTE_SEMANTIC", "0");
        if (IndexOnBattery) Environment.SetEnvironmentVariable("PALETTE_SEM_ON_BATTERY", "1");
        // The chat model thinks before it answers; llama-server reads its limit from these (the engine starts
        // the server from this process, so it inherits them). A small model left to think goes on for minutes.
        if (Environment.GetEnvironmentVariable("LLAMA_ARG_THINK_BUDGET") is null)
        {
            Environment.SetEnvironmentVariable("LLAMA_ARG_THINK_BUDGET", "400");
            Environment.SetEnvironmentVariable("LLAMA_ARG_THINK_BUDGET_MESSAGE", "That is enough thinking; I will answer now.");
        }
    }

    /// <summary>The taskbar search mode before we hid it, put back when the option is turned off.</summary>
    public int? SavedSearchboxMode { get; set; }

    private static string FilePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "palette", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath)) return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception ex) { App.Log("settings unreadable: " + ex.Message); }
        return new();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { App.Log("settings not saved: " + ex.Message); }
    }

    // ---- taskbar search box ----------------------------------------------------------------

    private const string SearchKey = @"Software\Microsoft\Windows\CurrentVersion\Search";
    private const string SearchValue = "SearchboxTaskbarMode";   // 0 hidden, 1 icon, 2 box, 3 icon and label

    /// <summary>Whether Windows' search box is off the taskbar, however it got that way.</summary>
    public static bool TaskbarSearchHidden()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(SearchKey);
            return key?.GetValue(SearchValue) is 0;
        }
        catch { return false; }
    }

    /// <summary>Hide or restore the taskbar search box. Explorer picks the change up live.</summary>
    public void ApplyTaskbarSearch(bool hide)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(SearchKey);
            int current = key.GetValue(SearchValue) is int v ? v : 2;
            if (hide)
            {
                if (current != 0) SavedSearchboxMode = current;
                key.SetValue(SearchValue, 0, RegistryValueKind.DWord);
            }
            else if (current == 0)
            {
                key.SetValue(SearchValue, SavedSearchboxMode ?? 1, RegistryValueKind.DWord);   // unknown: the icon
                SavedSearchboxMode = null;
            }
            Save();
            Win32.BroadcastSettingChange("TraySettings");
        }
        catch (Exception ex) { App.Log("taskbar search toggle failed: " + ex.Message); }
    }
}
