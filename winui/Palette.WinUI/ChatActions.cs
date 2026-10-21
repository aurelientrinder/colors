using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Palette;

/// <summary>
/// Something the chat does on the PC, as the answer shows it. While <see cref="State"/> is "asking" it is
/// a card that asks first (Allow or Deny); after, a quiet line saying what was done, with Undo. States:
/// asking, done, denied, stopped, failed, undone.
/// </summary>
public sealed class ActionBlock
{
    public string Kind { get; init; } = "";
    /// <summary>What it asks ("Move 3 files to Documents › Invoices?").</summary>
    public string Question { get; init; } = "";
    /// <summary>The files it is about, shown as links on the card.</summary>
    public List<string> Paths { get; init; } = new();
    public string State { get; set; } = "done";
    /// <summary>What happened, for the line ("Moved 3 files to Documents › Invoices").</summary>
    public string Message { get; set; } = "";
    public ActionRecord? Record { get; set; }

    /// <summary>The user's answer while it asks (not kept).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public TaskCompletionSource<bool>? Answer { get; set; }

    /// <summary>The state changed: its view draws itself again.</summary>
    public event Action? Changed;

    public void Set(string state, string? message = null)
    {
        State = state;
        if (message != null) Message = message;
        Changed?.Invoke();
    }

    /// <summary>Allow (true) or Deny (false), from its buttons or Enter.</summary>
    public void Decide(bool allow, bool always = false)
    {
        if (Answer == null) return;
        Always = allow && always;
        Answer.TrySetResult(allow);
    }

    /// <summary>Shown word for word on the card (a web search's query: what leaves the PC).</summary>
    public string Detail { get; init; } = "";
    /// <summary>What is being done once allowed ("Moving 3 files…"), on the line that takes the card's place.</summary>
    public string Working { get; init; } = "";
    /// <summary>Small print on the card, under what it is about.</summary>
    public string Note { get; init; } = "";
    /// <summary>The card offers "Allow for this chat" besides "Allow once"; <see cref="Always"/> says which was chosen.</summary>
    public bool OfferAlways { get; init; }
    public bool Always { get; private set; }
}

/// <summary>
/// The chat acting on the PC, with three levels of care. Opening an app or a file just happens.
/// Changing a setting (volume, Wi-Fi, dark mode…) and making a folder happen at once, with Undo under the
/// answer. Moving, renaming and recycling files ask first: the answer shows a card with the files, and the
/// model waits for Allow (or Enter) or Deny (or Esc). Everything done is logged (ActionLog) and can be
/// undone from its line, or by asking. Only the user's own files are touched (ActionLog.Forbidden), and
/// nothing is overwritten or deleted for good.
/// </summary>
public sealed partial class MainWindow
{
    /// <summary>The answer the tools are working for (set by RunChat around each call).</summary>
    private (ChatAnswer chat, ChatMsg answer)? _acting;

    /// <summary>The card waiting for Allow or Deny, if any: Enter allows it.</summary>
    private ActionBlock? _pendingApproval;

    /// <summary>Tools that do what was asked: once a round has done only these, the model just says so.</summary>
    /// (Making a folder is not one: files are often moved into it next.)
    private static readonly HashSet<string> Doing = new()
    {
        "set_reminder", "list_reminders", "cancel_reminder", "open", "change_setting", "move_files", "rename_file",
        "recycle_files", "undo_last_action",
    };

    /// <summary>
    /// Does the message being answered ask for something to be done? A question ("is Wi-Fi on?", "how do I
    /// move…") does not: the model, which reads one as a request, is then told nothing was changed.
    /// </summary>
    private bool _mayAct;

    private static bool AsksToAct(string message) =>
        AsksAction(message) || AsksReminder(message) || !(message.TrimEnd().EndsWith('?') || Regex.IsMatch(message,
            @"^\s*(how|what|why|when|where|which|who|is|are|does|do i|can i|should i|comment|pourquoi|quand|est-ce)\b", RegexOptions.IgnoreCase));

    private const string OnlyAsked = "Nothing was done: the user asked a question, not for a change. Answer it, and offer to do it.";

    // ------------------------------------------------------------------ asking first

    /// <summary>Show <paramref name="block"/> as a card under the answer and wait for Allow or Deny (Esc stops: Deny).</summary>
    private async Task<bool> AskFirst(ActionBlock block, CancellationToken ct)
    {
        block.State = "asking";
        block.Answer = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_acting is { } a)
        {
            a.answer.Blocks.Add(block);
            RenderChat(a.chat);
        }
        _pendingApproval = block;
        try
        {
            var stop = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using (ct.Register(() => stop.TrySetCanceled(ct)))
            {
                var won = await Task.WhenAny(block.Answer.Task, stop.Task);
                if (won != block.Answer.Task)
                {
                    block.Set("stopped", "Stopped: nothing was changed");
                    ct.ThrowIfCancellationRequested();
                }
            }
            bool allow = await block.Answer.Task;
            if (!allow) block.Set("denied", block.Kind == "web" ? "You declined: nothing was sent" : "You declined: nothing was changed");
            // Allowed: the card goes at once, for a line saying what is being done until it is.
            else block.Set("working", block.Working.Length > 0 ? block.Working : "Working on it…");
            return allow;
        }
        finally
        {
            if (_pendingApproval == block) _pendingApproval = null;
            block.Answer = null;
        }
    }

    // ------------------------------------------------------------------ finding what the model names

    /// <summary>Every file the tools found in this conversation, by path.</summary>
    private IEnumerable<string> FoundPaths()
    {
        if (_acting is not { } a) yield break;
        foreach (var m in a.chat.Messages)
            foreach (var b in m.Blocks)
            {
                if (b is FilesBlock f) foreach (var x in f.Files) yield return x.Path;
                if (b is SourcesBlock s) foreach (var x in s.Docs) yield return x.Path;
                if (b is ActionBlock { State: "done" } ab && ab.Record?.Moves is { } moves) foreach (var mv in moves) yield return mv.To;
            }
    }

    /// <summary>
    /// The file the model means by <paramref name="name"/>: a path, or the name (with or without its
    /// extension) of one its tools found in this conversation, the latest first. Null, with why, if none.
    /// </summary>
    private string? ResolveFile(string name, out string why)
    {
        why = "";
        name = name.Trim().Trim('"', '“', '”');
        if (ActionLog.IsNetwork(name))
        {
            // Not even looked at: checking that it exists would already reach the server.
            why = "I only work with files on this PC, not on network locations.";
            return null;
        }
        if (Path.IsPathRooted(name) && ActionLog.Exists(name)) return Path.GetFullPath(name);
        var found = FoundPaths().Reverse().Distinct(StringComparer.OrdinalIgnoreCase).Where(ActionLog.Exists).ToList();
        var hit = found.FirstOrDefault(p => Path.GetFileName(p).Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? found.FirstOrDefault(p => Path.GetFileNameWithoutExtension(p).Equals(name, StringComparison.OrdinalIgnoreCase));
        hit ??= TypedPath(name);
        if (hit == null) why = $"I don't know which file {name} is: find it with find_files first, then use its exact name.";
        return hit;
    }

    /// <summary>
    /// A full path the user typed in this conversation that ends in <paramref name="name"/> (the model often
    /// passes only the name of "C:\…\notes.txt"). Paths may hold spaces, so each drive letter is followed to
    /// the name rather than parsed.
    /// </summary>
    private string? TypedPath(string name)
    {
        if (_acting is not { } a || name.Length == 0 || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return null;
        foreach (var m in a.chat.Messages.Where(m => m.Role == "user").Reverse())
            foreach (Match drive in Regex.Matches(m.Text, @"\b[A-Za-z]:\\"))
            {
                int end = m.Text.IndexOf("\\" + name, drive.Index, StringComparison.OrdinalIgnoreCase);
                if (end < 0 || m.Text[drive.Index..end].IndexOfAny(['\n', '"', '<', '>', '|']) >= 0) continue;
                string path = m.Text[drive.Index..(end + 1 + name.Length)];
                if (ActionLog.Exists(path)) return Path.GetFullPath(path);
            }
        return null;
    }

    /// <summary>
    /// The folder <paramref name="to"/> names: a path; a usual folder (Documents), or one inside it
    /// (Documents/Invoices); or a plain name, looked for beside <paramref name="near"/> and in the usual
    /// folders. A folder that does not exist yet is returned too (made when the action is allowed), in the
    /// files' own folder for a plain name. Null, with why, when it cannot be.
    /// </summary>
    private static string? ResolveFolder(string to, string? near, out string why)
    {
        why = "";
        to = to.Trim().Trim('"', '“', '”').Replace('/', '\\').Replace(" › ", "\\").Replace("›", "\\").TrimEnd('\\');
        if (to.Length == 0)
        {
            why = "Say which folder (to).";
            return null;
        }
        string? folder = null;
        if (Path.IsPathRooted(to)) folder = Path.GetFullPath(to);
        else
        {
            var parts = to.Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (ActionLog.Known(parts[0]) is { } root) folder = Path.Combine(new[] { root }.Concat(parts[1..]).ToArray());
            else if (parts.Length == 1)
            {
                var places = new[] { near, ActionLog.Known("documents"), ActionLog.Known("desktop"), ActionLog.Known("downloads"), ActionLog.Known("pictures") }
                    .Where(p => p != null).Select(p => Path.Combine(p!, to));
                folder = places.FirstOrDefault(Directory.Exists) ?? Path.Combine(near ?? ActionLog.Known("documents")!, to);
            }
            else folder = Path.Combine(near ?? ActionLog.Known("documents")!, to);
        }
        if (ActionLog.Forbidden(folder) is { } no)
        {
            why = $"That folder is {no}: I only work in the user's own folders.";
            return null;
        }
        if (File.Exists(folder))
        {
            why = $"{Path.GetFileName(folder)} is a file, not a folder.";
            return null;
        }
        return folder;
    }

    private static List<string> Strings(JsonElement args, string key)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var v)) return new();
        if (v.ValueKind == JsonValueKind.String) return new() { v.GetString()! };
        if (v.ValueKind != JsonValueKind.Array) return new();
        return v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).Where(s => s.Trim().Length > 0).ToList();
    }

    private static string Files(int n) => n == 1 ? "1 file" : $"{n} files";

    private string? ChatId => _acting?.chat.Id;

    // ------------------------------------------------------------------ the tools

    /// <summary>Open an app (by its name, as the palette finds it), a file the tools found, or a folder.</summary>
    private async Task<(string, object?, string?)> OpenTool(string target, CancellationToken ct)
    {
        if (!_mayAct) return (OnlyAsked, null, null);
        if (target.Length == 0) return ("Say what to open (target).", null, null);
        string? path = ResolveFile(target, out _) ?? (ActionLog.Known(target) is { } k ? k : null);
        if (path != null)
        {
            // A program or script runs rather than opens, and the name may have come from a page or a
            // document the chat read rather than from the user: it asks first.
            if (ActionLog.IsRunnable(path))
            {
                var block = new ActionBlock
                {
                    Kind = "run", Paths = new() { path }, Question = $"Run {Path.GetFileName(path)}?", Working = "Starting it…",
                    Note = "It is a program or script: it can change anything you can.",
                };
                if (!await AskFirst(block, ct)) return ("The user declined: it was not run.", block, null);
                DispatcherQueue.TryEnqueue(() => OpenFromChat(path, reveal: false));
                block.Set("done", $"Ran {Path.GetFileName(path)}");
                ActionLog.Add(new ActionRecord(ActionLog.NewId(), "open", $"Ran {Path.GetFileName(path)}", DateTime.Now, ChatId));
                return ($"Started {Path.GetFileName(path)}.", block, null);
            }
            DispatcherQueue.TryEnqueue(() => OpenFromChat(path, reveal: false));
            ActionLog.Add(new ActionRecord(ActionLog.NewId(), "open", $"Opened {Path.GetFileName(path)}", DateTime.Now, ChatId));
            return ($"Opened {Path.GetFileName(path)}.", null, null);
        }
        var found = await Task.Run(() =>
        {
            var r = Native.Search(target);
            int i = r.Hits.FindIndex(h => h.Kind is "App" or "Window" or "Setting");
            return (r, i);
        });
        if (found.i < 0) return ($"No app called {target} was found on this PC.", null, null);
        var hit = found.r.Hits[found.i];
        await Task.Run(() => Native.Launch(found.r.Gen, found.i, reveal: false));
        ActionLog.Add(new ActionRecord(ActionLog.NewId(), "open", $"Opened {hit.Title}", DateTime.Now, ChatId));
        return ($"Opened {hit.Title}.", null, null);
    }

    /// <summary>The quick settings the chat may change: its name, the engine's key, and how a person says it.</summary>
    private static readonly Dictionary<string, (string key, string label)> Settable = new()
    {
        ["volume"] = ("volume", "Volume"), ["mute"] = ("mute", "Sound"), ["brightness"] = ("brightness", "Brightness"),
        ["wifi"] = ("wifi", "Wi-Fi"), ["bluetooth"] = ("bluetooth", "Bluetooth"), ["dark_mode"] = ("dark", "Dark mode"),
        ["do_not_disturb"] = ("dnd", "Do not disturb"),
    };

    /// <summary>Change a quick setting, keeping how it was for Undo.</summary>
    private async Task<(string, object?, string?)> SettingTool(string setting, string value)
    {
        setting = setting.Trim().ToLowerInvariant().Replace(' ', '_').Replace("-", "");
        if (setting == "sound") setting = "mute";
        if (!Settable.TryGetValue(setting, out var s)) return ($"There is no setting called {setting}: use one of {string.Join(", ", Settable.Keys)}.", null, null);
        value = value.Trim().ToLowerInvariant().TrimEnd('%');
        if (value is "true" or "yes" or "enable" or "enabled") value = "on";
        if (value is "false" or "no" or "disable" or "disabled") value = "off";
        bool level = s.key is "volume" or "brightness";
        if (level ? !(value is "up" or "down" || int.TryParse(value, out _)) : value is not ("on" or "off"))
            return ($"The value for {setting} is {(level ? "a level from 0 to 100, up or down" : "on or off")}.", null, null);
        if (level && int.TryParse(value, out int n)) value = Math.Clamp(n, 0, 100).ToString(CultureInfo.InvariantCulture);

        string controlKey = s.key == "mute" ? "volume" : s.key;
        var before = await Task.Run(() => Native.QuickControl(controlKey));
        if (before == null) return ($"{s.label} can't be changed on this PC.", null, null);
        if (!_mayAct)
        {
            // A question about it ("is Wi-Fi on?"): what it is now, and nothing changed.
            string state = s.key switch
            {
                "volume" or "mute" => $"the volume is {before.Value}%{(before.On ? ", muted" : "")}",
                "brightness" => $"the brightness is {before.Value}%",
                _ => $"{s.label} is {(before.On ? "on" : "off")}",
            };
            return ($"Nothing was changed (the user asked a question): {state}. Answer from that, and offer to change it.", null, null);
        }
        string was = s.key switch
        {
            "volume" => $"volume:{before.Value}",
            "brightness" => $"brightness:{before.Value}",
            "mute" => $"mute:{(before.On ? "on" : "off")}",
            _ => $"{s.key}:{(before.On ? "on" : "off")}",
        };
        if (!await Task.Run(() => Native.Quick($"{s.key}:{value}"))) return ($"{s.label} could not be changed.", null, null);
        var after = await Task.Run(() => Native.QuickControl(controlKey));
        string now = s.key switch
        {
            "volume" or "brightness" => $"{s.label} set to {after?.Value ?? 0}% (was {before.Value}%)",
            "mute" => after?.On ?? value == "on" ? "Sound muted" : "Sound on",
            _ => $"{s.label} turned {value}",
        };
        var record = ActionLog.Add(new ActionRecord(ActionLog.NewId(), "setting", now, DateTime.Now, ChatId, Before: was));
        return ($"Done: {now}. The user can undo it.", new ActionBlock { Kind = "setting", State = "done", Message = now, Record = record }, null);
    }

    /// <summary>Make a folder (at once: an empty folder harms nothing, and Undo removes it while empty).</summary>
    private (string, object?, string?) CreateFolderTool(string name, string inside)
    {
        if (!_mayAct) return (OnlyAsked, null, null);
        if (name.Trim().Length == 0) return ("Say the folder's name (name).", null, null);
        var folder = ResolveFolder(inside.Trim().Length > 0 ? Path.Combine(inside.Trim(), name.Trim()) : name, null, out string why);
        if (folder == null) return ($"Not made: {why}", null, null);
        if (Directory.Exists(folder)) return ($"The folder {ActionLog.Show(folder)} is there already.", null, null);
        Directory.CreateDirectory(folder);
        string msg = $"Made the folder {ActionLog.Show(folder)}";
        var record = ActionLog.Add(new ActionRecord(ActionLog.NewId(), "folder", msg, DateTime.Now, ChatId, MadeFolder: folder));
        return ($"Done: {msg}. The user can undo it.", new ActionBlock { Kind = "folder", State = "done", Message = msg, Record = record, Paths = new() { folder } }, null);
    }

    /// <summary>Move files into a folder (made if need be), after the user allows it.</summary>
    private async Task<(string, object?, string?)> MoveTool(List<string> names, string to, CancellationToken ct)
    {
        if (!_mayAct) return (OnlyAsked, null, null);
        if (names.Count == 0) return ("Say which files to move (files).", null, null);
        var paths = new List<string>();
        foreach (var n in names)
        {
            if (ResolveFile(n, out string why) is not { } p) return ($"Nothing moved: {why}", null, null);
            if (ActionLog.Forbidden(p) is { } no) return ($"Nothing moved: {Path.GetFileName(p)} is in {no}.", null, null);
            if (!paths.Contains(p, StringComparer.OrdinalIgnoreCase)) paths.Add(p);
        }
        var parents = paths.Select(p => Path.GetDirectoryName(p)!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var folder = ResolveFolder(to, parents.Count == 1 ? parents[0] : null, out string whyNot);
        if (folder == null) return ($"Nothing moved: {whyNot}", null, null);
        paths = paths.Where(p => !string.Equals(Path.GetDirectoryName(p), folder, StringComparison.OrdinalIgnoreCase)).ToList();
        if (paths.Count == 0) return ($"Those files are in {ActionLog.Show(folder)} already.", null, null);
        if (paths.Any(p => Directory.Exists(p) && (folder + "\\").StartsWith(p + "\\", StringComparison.OrdinalIgnoreCase)))
            return ("Nothing moved: a folder can't go inside itself.", null, null);

        bool make = !Directory.Exists(folder);
        var block = new ActionBlock
        {
            Kind = "move", Paths = paths, Working = $"Moving {Files(paths.Count)}…",
            Question = make ? $"Make the folder {ActionLog.Show(folder)} and move {Files(paths.Count)} into it?"
                : $"Move {Files(paths.Count)} to {ActionLog.Show(folder)}?",
        };
        if (!await AskFirst(block, ct)) return ("The user declined: nothing was moved.", block, null);

        if (make) Directory.CreateDirectory(folder);
        var moves = new List<FileMove>();
        var failed = new List<string>();
        foreach (var p in paths)
        {
            try
            {
                string dest = ActionLog.FreeName(folder, Path.GetFileName(p));
                ActionLog.MovePath(p, dest);
                moves.Add(new FileMove(p, dest));
            }
            catch (Exception ex)
            {
                App.Log($"move {p}: {ex.Message}");
                failed.Add(Path.GetFileName(p));
            }
        }
        if (moves.Count == 0)
        {
            if (make) try { Directory.Delete(folder); } catch (IOException) { }
            block.Set("failed", "Couldn't move them: " + string.Join(", ", failed) + " may be open in another app");
            return ("Nothing could be moved (the files may be open in another app).", block, null);
        }
        string msg = $"Moved {Files(moves.Count)} to {ActionLog.Show(folder)}" + (failed.Count > 0 ? $" ({failed.Count} couldn't be moved)" : "");
        block.Record = ActionLog.Add(new ActionRecord(ActionLog.NewId(), "move", msg, DateTime.Now, ChatId, Moves: moves, MadeFolder: make ? folder : null));
        block.Paths.Clear();
        block.Paths.AddRange(moves.Select(m => m.To));
        block.Set("done", msg);
        return ($"Done: {msg}" + (failed.Count > 0 ? $"; these could not be moved (open in another app?): {string.Join(", ", failed)}" : "") +
            ". Their names are unchanged. The user can undo it.", block, null);
    }

    /// <summary>Rename a file (its extension kept if the new name leaves it out), after the user allows it.</summary>
    private async Task<(string, object?, string?)> RenameTool(string name, string newName, CancellationToken ct)
    {
        if (!_mayAct) return (OnlyAsked, null, null);
        if (ResolveFile(name, out string why) is not { } p) return ($"Not renamed: {why}", null, null);
        if (ActionLog.Forbidden(p) is { } no) return ($"Not renamed: it is in {no}.", null, null);
        newName = newName.Trim().Trim('"', '“', '”');
        string ext = Path.GetExtension(p);
        if (File.Exists(p) && ext.Length > 0 && !newName.EndsWith(ext, StringComparison.OrdinalIgnoreCase) && Path.GetExtension(newName).Length == 0) newName += ext;
        if (newName.Length == 0 || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || newName.EndsWith('.'))
            return ($"Not renamed: {newName} can't be a file name (no \\ / : * ? \" < > |).", null, null);
        string dest = Path.Combine(Path.GetDirectoryName(p)!, newName);
        if (dest.Equals(p, StringComparison.Ordinal)) return ("It already has that name.", null, null);
        if (ActionLog.Exists(dest) && !dest.Equals(p, StringComparison.OrdinalIgnoreCase)) return ($"Not renamed: there is already a {newName} in that folder.", null, null);

        var block = new ActionBlock { Kind = "rename", Paths = new() { p }, Question = $"Rename {Path.GetFileName(p)} to {newName}?", Working = "Renaming it…" };
        if (!await AskFirst(block, ct)) return ("The user declined: the name was not changed.", block, null);
        try
        {
            ActionLog.MovePath(p, dest);
        }
        catch (Exception ex)
        {
            block.Set("failed", "Couldn't rename it: " + ex.Message);
            return ("It could not be renamed (it may be open in another app).", block, null);
        }
        string msg = $"Renamed {Path.GetFileName(p)} to {newName}";
        block.Record = ActionLog.Add(new ActionRecord(ActionLog.NewId(), "rename", msg, DateTime.Now, ChatId, Moves: new() { new FileMove(p, dest) }));
        block.Paths[0] = dest;
        block.Set("done", msg);
        return ($"Done: {msg}. The user can undo it.", block, null);
    }

    /// <summary>Put files in the Recycle Bin (never deleted for good), after the user allows it.</summary>
    private async Task<(string, object?, string?)> RecycleTool(List<string> names, CancellationToken ct)
    {
        if (!_mayAct) return (OnlyAsked, null, null);
        if (names.Count == 0) return ("Say which files to recycle (files).", null, null);
        var paths = new List<string>();
        foreach (var n in names)
        {
            if (ResolveFile(n, out string why) is not { } p) return ($"Nothing recycled: {why}", null, null);
            if (ActionLog.Forbidden(p) is { } no) return ($"Nothing recycled: {Path.GetFileName(p)} is in {no}.", null, null);
            if (!paths.Contains(p, StringComparer.OrdinalIgnoreCase)) paths.Add(p);
        }
        var block = new ActionBlock
        {
            Kind = "recycle", Paths = paths, Question = $"Put {Files(paths.Count)} in the Recycle Bin?", Working = $"Putting {Files(paths.Count)} in the Recycle Bin…",
        };
        if (!await AskFirst(block, ct)) return ("The user declined: nothing was recycled.", block, null);
        var done = await Task.Run(() => ActionLog.Recycle(paths));
        if (done.Count == 0)
        {
            block.Set("failed", "Couldn't recycle them: they may be open in another app");
            return ("Nothing could be recycled (the files may be open in another app).", block, null);
        }
        string msg = $"Put {Files(done.Count)} in the Recycle Bin" + (done.Count < paths.Count ? $" ({paths.Count - done.Count} couldn't be)" : "");
        block.Record = ActionLog.Add(new ActionRecord(ActionLog.NewId(), "recycle", msg, DateTime.Now, ChatId, Recycled: done));
        block.Set("done", msg);
        return ($"Done: {msg}. The user can undo it (they come back from the Recycle Bin).", block, null);
    }

    /// <summary>"Undo that": the latest thing done that can be undone (in this chat first).</summary>
    private (string, object?, string?) UndoTool()
    {
        if (ActionLog.LastUndoable(ChatId) is not { } r) return ("There is nothing to undo.", null, null);
        var (ok, message) = ActionLog.Undo(r);
        // The line under the answer that did it says so too.
        if (ok && _acting is { } a)
            foreach (var b in a.chat.Messages.SelectMany(m => m.Blocks).OfType<ActionBlock>().Where(b => b.Record?.Id == r.Id))
                b.Set("undone", "Undone: " + b.Message);
        return ok ? ($"Undone ({r.Summary}): {message}.", new ActionBlock { Kind = "undo", State = "done", Message = $"Undid: {r.Summary}" }, null)
            : ($"Could not undo {r.Summary}: {message}.", null, null);
    }

    // ------------------------------------------------------------------ the card and the line

    /// <summary>
    /// The view of an action under the answer: while it asks, a card with the question, the files as links,
    /// Deny and Allow; once done, a quiet line with Undo; declined, stopped or failed, a line saying so.
    /// </summary>
    private FrameworkElement ActionView(ActionBlock block)
    {
        var host = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
        string drawn = block.State;
        void Draw()
        {
            if (block.State == drawn && block.State != "working") return;
            drawn = block.State;
            var next = block.State == "asking" ? AskCard(block) : ActionLine(block);
            // The card gives way to its line at once, which fades in as the card's room closes.
            next.Opacity = 0;
            host.Content = next;
            FadeIn(next);
            if (_chat is { } c) DispatcherQueue.TryEnqueue(() => RenderChat(c));
        }
        block.Changed += () => DispatcherQueue.TryEnqueue(Draw);
        host.Content = block.State == "asking" ? AskCard(block) : ActionLine(block);
        if (block.State == "asking")
        {
            // The card has just come: bring it into view once it is laid out, wherever the reading was.
            host.Opacity = 0;
            host.Loaded += (_, _) =>
            {
                FadeIn(host);
                _glideToEnd = true;
                if (_chat is { } c) RenderChat(c);
            };
        }
        return host;
    }

    private static void FadeIn(UIElement e)
    {
        var a = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = 0, To = 1, Duration = TimeSpan.FromMilliseconds(180),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut },
        };
        var sb = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(a, e);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(a, "Opacity");
        sb.Children.Add(a);
        sb.Begin();
    }

    /// <summary>
    /// The card that asks first, as the bubble's card: a small header (whose wish it is), the question,
    /// what it is about (the files as links, or the query word for word in a soft box), one line of small
    /// print, and the choices on the right: Deny, quiet; Allow (once), the accent. Enter allows, Esc stops.
    /// </summary>
    private FrameworkElement AskCard(ActionBlock block)
    {
        var panel = new StackPanel { Spacing = 10, Padding = new Thickness(16, 12, 14, 12) };
        bool web = block.Kind == "web";
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new FontIcon
        {
            Glyph = web ? "" : "", FontSize = 13, Foreground = Res("AccentTextFillColorPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(new TextBlock
        {
            Text = web ? "Palette wants to use the web" : "Palette wants to change your files", FontSize = 12,
            Foreground = Res("TextFillColorSecondaryBrush"), VerticalAlignment = VerticalAlignment.Center,
        });
        panel.Children.Add(head);
        panel.Children.Add(new TextBlock { Text = block.Question, FontSize = 15, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, -2, 0, 0) });
        if (block.Paths.Count > 0)
        {
            var files = new StackPanel { Spacing = 5 };
            foreach (var p in block.Paths.Take(6))
            {
                var link = MakeFileLink(p, Path.GetFileName(p));
                link.Margin = new Thickness(0);
                link.HorizontalAlignment = HorizontalAlignment.Left;
                files.Children.Add(link);
            }
            if (block.Paths.Count > 6)
                files.Children.Add(new TextBlock { Text = $"and {block.Paths.Count - 6} more", FontSize = 12, Foreground = Res("TextFillColorTertiaryBrush") });
            panel.Children.Add(files);
        }
        if (block.Detail.Length > 0)
        {
            // What leaves the PC, word for word.
            panel.Children.Add(new Border
            {
                Background = Res("SubtleFillColorSecondaryBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 9),
                Child = new TextBlock { Text = block.Detail, FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
            });
        }
        string note = block.Note.Length > 0 ? block.Note
            : block.Kind == "recycle" ? "They go to the Recycle Bin, and you can undo it afterwards." : "You can undo it afterwards.";
        panel.Children.Add(new TextBlock
        {
            Text = note, FontSize = 12, LineHeight = 17, TextWrapping = TextWrapping.Wrap, Foreground = Res("TextFillColorTertiaryBrush"), Margin = new Thickness(0, -2, 0, 0),
        });

        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 2, 0, 0) };
        Button Choice(string text, Action click, string? keys = null)
        {
            var b = new Button { Content = text, Height = 32, Padding = new Thickness(14, 0, 14, 0), CornerRadius = new CornerRadius(6), FontSize = 13 };
            b.Click += (_, _) => click();
            if (keys != null) ToolTipService.SetToolTip(b, keys);
            row.Children.Add(b);
            return b;
        }
        var deny = Choice("Deny", () => block.Decide(false), "Esc");
        deny.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        deny.BorderThickness = new Thickness(0);
        if (block.OfferAlways) Choice("Allow for this chat", () => block.Decide(true, always: true));
        var allow = Choice(block.OfferAlways ? "Allow once" : "Allow", () => block.Decide(true), "Enter");
        allow.Style = (Style)Application.Current.Resources["AccentButtonStyle"];
        allow.FontWeight = FontWeights.SemiBold;
        panel.Children.Add(row);

        return new Border
        {
            Background = Res("CardBackgroundFillColorDefaultBrush"), BorderBrush = Res("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12), Margin = new Thickness(0, 6, 0, 4), Child = panel,
        };
    }

    private static FrameworkElement ActionLine(ActionBlock block)
    {
        // Allowed and being done, or a search done: the step on the answer's rail already says it, so the card
        // just leaves. (A file action comes back as its line, with Undo, once done.)
        if (block.State == "working" || block.Kind == "web" && block.State == "done") return new Grid();
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(0, 2, 0, 0) };
        string glyph = block.State switch { "done" => "", "undone" => "", "failed" => "", _ => "" };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 11, Foreground = Res("TextFillColorTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center });
        var text = new TextBlock
        {
            Text = block.Message, FontSize = 11, Foreground = Res("TextFillColorTertiaryBrush"), VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 400,
        };
        ToolTipService.SetToolTip(text, block.Message);
        row.Children.Add(text);
        var current = block.Record is { } r ? ActionLog.Get(r.Id) ?? r : null;
        if (block.State == "done" && current is { CanUndo: true })
        {
            var undo = new HyperlinkButton { Content = "Undo", FontSize = 11, Padding = new Thickness(4, 0, 4, 0), MinHeight = 0, VerticalAlignment = VerticalAlignment.Center };
            undo.Click += async (_, _) =>
            {
                undo.IsEnabled = false;
                var (ok, message) = await Task.Run(() => ActionLog.Undo(current));
                if (ok) block.Set("undone", $"Undone: {block.Message}");
                else
                {
                    text.Text = message;
                    undo.IsEnabled = true;
                }
            };
            row.Children.Add(undo);
        }
        else if (block.State == "done" && current is { Undone: true })
        {
            text.Text = $"Undone: {block.Message}";
        }
        return row;
    }
}
