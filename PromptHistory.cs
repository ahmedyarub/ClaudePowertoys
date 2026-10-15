using System.IO;
using System.Windows.Automation;
using static ClaudePowertoys.KeyboardHook;

namespace ClaudePowertoys;

/// <summary>
/// Your earlier prompts, newest first, across every Claude Code session of the last 30 days (from the
/// transcripts in ~/.claude/projects; each file is re-read only when it changes).
/// </summary>
static class PromptHistory
{
    const int MaxEntries = 300;
    static readonly TimeSpan MaxAge = TimeSpan.FromDays(30);
    static readonly string ProjectsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    static readonly Dictionary<string, (DateTime WriteTime, List<(long At, string Text)> Prompts)> Cache = new(StringComparer.OrdinalIgnoreCase);
    static readonly object Gate = new();

    public static List<string> Newest()
    {
        lock (Gate)
        {
            if (!Directory.Exists(ProjectsRoot)) return [];
            var since = DateTime.UtcNow - MaxAge;
            var all = new List<(long At, string Text)>();
            foreach (var file in Directory.EnumerateFiles(ProjectsRoot, "*.jsonl", SearchOption.AllDirectories))
            {
                var writeTime = File.GetLastWriteTimeUtc(file);
                if (writeTime < since) continue;
                if (!Cache.TryGetValue(file, out var entry) || entry.WriteTime != writeTime)
                {
                    try { Cache[file] = entry = (writeTime, ConversationPrompts.ReadTimed(file)); }
                    catch (IOException) { continue; }
                }
                all.AddRange(entry.Prompts);
            }
            return all.OrderByDescending(p => p.At)
                .Select(p => p.Text.Trim())
                .Distinct()
                .Take(MaxEntries)
                .ToList();
        }
    }
}

/// <summary>
/// Alt+Up in Claude's prompt box opens a list of your earlier prompts above it; Up / Down move while
/// Alt is held, Esc cancels, and releasing Alt types the chosen prompt at the cursor. Works like the
/// Ctrl+Tab switcher because nothing can be typed while Alt is held without turning into shortcuts.
/// </summary>
sealed class PromptHistoryRecall : IDisposable
{
    const int VK_UP = 0x26, VK_DOWN = 0x28, VK_LMENU = 0xA4, VK_RMENU = 0xA5;

    readonly KeyboardHook _hook;
    readonly ListPopup _popup = new("Earlier prompts");
    List<string>? _entries;
    int _selected;

    public volatile bool Enabled;

    public PromptHistoryRecall()
    {
        _ = _popup.Handle;
        _hook = new KeyboardHook("Prompt history", (message, vk) => Enabled && Handle(message, vk));
        // Reading every transcript the first time takes a moment; do it before the first Alt+Up.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { PromptHistory.Newest(); }
            catch (Exception ex) { Log.Write($"Prompt history: {ex.Message}"); }
        });
    }

    public void Dispose()
    {
        _hook.Dispose();
        _popup.Dispose();
    }

    bool Handle(int message, int vk)
    {
        bool down = IsDown(message);
        if (_entries != null)
        {
            if (vk is VK_UP or VK_DOWN)
            {
                if (down) Move(vk == VK_UP ? 1 : -1);
                return true;
            }
            if (vk == VK_ESCAPE)
            {
                if (down) Close(insert: false);
                return true;
            }
            if (IsUp(message) && vk is VK_MENU or VK_LMENU or VK_RMENU)
            {
                Close(insert: true);
                return false;
            }
            return false;
        }

        if (!down || vk != VK_UP || !KeyHeld(VK_MENU) || KeyHeld(VK_CONTROL) || KeyHeld(VK_SHIFT)) return false;
        if (!ClaudeWindow.IsInFront() || !FocusTracker.InPromptBox) return false;

        // The app would see Alt pressed and released alone, which opens its menu bar.
        TextTyper.MaskAltRelease();
        _entries = [];
        _selected = 0;
        Ui.Post(_popup, Open);
        return true;
    }

    /// <summary>Runs on the UI thread.</summary>
    void Open()
    {
        var entries = PromptHistory.Newest();
        if (_entries == null) return;
        _entries = entries;
        // Drawn oldest at the top, newest at the bottom next to the prompt box, so Up (older) moves up.
        _popup.ShowList(ClaudeWindow.Find(), PromptBoxTop(), Enumerable.Reverse(entries).Select(OneLine).ToList(), Row(_selected));
    }

    void Move(int step)
    {
        if (_entries == null || _entries.Count == 0) return;
        _selected = Math.Clamp(_selected + step, 0, _entries.Count - 1);
        int row = Row(_selected);
        Ui.Post(_popup, () => _popup.Select(row));
    }

    /// <summary>Where entry <paramref name="index"/> (0 = newest) is drawn in the reversed list.</summary>
    int Row(int index) => (_entries?.Count ?? 0) - 1 - index;

    void Close(bool insert)
    {
        var chosen = insert && _entries is { Count: > 0 } ? _entries[_selected] : null;
        _entries = null;
        Ui.Post(_popup, _popup.Conceal);
        if (chosen == null) return;
        // Typed once Alt is up: injected keys with Alt down would become Alt shortcuts.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            Thread.Sleep(50);
            Log.Write($"Prompt history: inserted a prompt of {chosen.Length} characters");
            TextTyper.Type(chosen);
        });
    }

    static string OneLine(string text)
    {
        var line = string.Join(" ", text.Split((char[])['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return line.Length <= 140 ? line : line[..139] + "…";
    }

    static int? PromptBoxTop()
    {
        var claude = ClaudeWindow.Find();
        if (claude == IntPtr.Zero) return null;
        var box = AutomationElement.FromHandle(claude).FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.NameProperty, "Prompt")));
        return box == null ? null : (int)box.Current.BoundingRectangle.Top;
    }
}
