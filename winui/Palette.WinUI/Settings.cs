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

    /// <summary>The line of keys for the selected row, in the search card.</summary>
    public bool KeyHints { get; set; } = true;

    /// <summary>
    /// When the palette opens it reads the window behind it: the text selected there (offered on Home)
    /// and its commands (found by typing). Nothing is kept or sent anywhere (see uia.rs).
    /// </summary>
    public bool ReadWindow { get; set; } = true;

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
