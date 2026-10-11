using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using static ClaudePowertoys.KeyboardHook;

namespace ClaudePowertoys;

/// <param name="Text">The message's text (its start, for a long one), as the transcript has it.</param>
/// <param name="PromptIndex">The prompt the message is, or the prompt Claude's reply answers; for scrolling back to it.</param>
sealed record Bookmark(string SessionId, string CliSessionId, string SessionTitle, bool FromUser, string Text, int PromptIndex, long AddedAt);

/// <summary>Bookmarked messages, newest first, in %APPDATA%\ClaudePowertoys\bookmarks.json.</summary>
static class BookmarkStore
{
    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudePowertoys", "bookmarks.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly object Gate = new();

    public static List<Bookmark> Load()
    {
        lock (Gate)
        {
            if (!File.Exists(FilePath)) return [];
            try { return JsonSerializer.Deserialize<List<Bookmark>>(File.ReadAllText(FilePath)) ?? []; }
            catch (JsonException ex)
            {
                Log.Write($"bookmarks.json is not valid JSON: {ex.Message}");
                return [];
            }
        }
    }

    /// <summary>Adds the bookmark, or removes it when that message is already bookmarked. Returns whether it was added.</summary>
    public static bool Toggle(Bookmark bookmark)
    {
        lock (Gate)
        {
            var all = Load();
            int existing = all.FindIndex(b => Same(b, bookmark));
            if (existing >= 0) all.RemoveAt(existing);
            else all.Insert(0, bookmark);
            Save(all);
            return existing < 0;
        }
    }

    public static void Remove(Bookmark bookmark)
    {
        lock (Gate)
        {
            var all = Load();
            all.RemoveAll(b => Same(b, bookmark));
            Save(all);
        }
    }

    static bool Same(Bookmark a, Bookmark b) =>
        a.CliSessionId == b.CliSessionId && a.FromUser == b.FromUser && a.PromptIndex == b.PromptIndex && a.Text == b.Text;

    static void Save(List<Bookmark> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(all, Json));
    }
}

/// <summary>
/// Ctrl+D in Claude bookmarks the message under the mouse, yours or Claude's (again removes it).
/// The message is the last "You said" / "Claude responded" heading above the pointer, matched to
/// the session's transcript. Left alone in other text fields (the terminal: end of input).
/// </summary>
sealed class BookmarkShortcut : IDisposable
{
    const int VK_D = 0x44;
    const int KeptChars = 600;

    readonly KeyboardHook _hook;
    readonly FlashTip _flash = new();
    int _busy;

    public volatile bool Enabled;

    public BookmarkShortcut()
    {
        _ = _flash.Handle;
        _hook = new KeyboardHook("Bookmarks", (message, vk) => Enabled && Handle(message, vk));
    }

    public void Dispose()
    {
        _hook.Dispose();
        _flash.Dispose();
    }

    bool Handle(int message, int vk)
    {
        if (vk != VK_D || !KeyHeld(VK_CONTROL) || KeyHeld(VK_SHIFT) || KeyHeld(VK_MENU) || KeyHeld(VK_LWIN) || KeyHeld(VK_RWIN)) return false;
        if (!ClaudeWindow.IsInFront() || FocusTracker.InOtherTextField) return false;
        if (IsDown(message) && Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
        {
            var at = Cursor.Position;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                string note;
                try { note = BookmarkAt(at); }
                catch (Exception ex) { note = "Couldn't bookmark"; Log.Write($"Bookmark failed: {ex.GetType().Name}: {ex.Message}"); }
                finally { Interlocked.Exchange(ref _busy, 0); }
                _flash.BeginInvoke(() => _flash.Show(note, at));
            });
        }
        return true;
    }

    static string BookmarkAt(Point at)
    {
        var claude = ClaudeWindow.Find();
        var headings = ConversationView.MessageHeadings(AutomationElement.FromHandle(claude));
        int index = headings.FindLastIndex(h => h.Current.BoundingRectangle.Top <= at.Y);
        if (index < 0) return "No message under the mouse";
        var heading = headings[index];
        bool fromUser = ConversationView.IsPrompt(heading);
        var start = ConversationView.Normalize(ConversationView.MessageStart(heading));

        var session = RecentSessions.OnScreen();
        var transcript = session == null ? null : ConversationPrompts.FindTranscript(session.Value.CliSessionId);
        if (transcript == null) return "No transcript for this session";

        var candidates = ConversationExport.Parts(transcript)
            .Where(p => p.FromUser == fromUser && ConversationView.Matches(ConversationView.Normalize(p.Text), start))
            .ToList();
        if (candidates.Count == 0)
        {
            Log.Write($"Bookmark: '{start}' not found in the transcript");
            return "Couldn't find that message";
        }
        // Messages with the same opening: count back from the newest loaded one.
        int laterSame = headings.Skip(index + 1)
            .Count(h => ConversationView.IsPrompt(h) == fromUser && ConversationView.Normalize(ConversationView.MessageStart(h)) == start);
        var part = candidates[Math.Max(0, candidates.Count - 1 - laterSame)];

        var text = part.Text.Length <= KeptChars ? part.Text : part.Text[..KeptChars] + "…";
        var s = session!.Value;
        var bookmark = new Bookmark(s.SessionId, s.CliSessionId, s.Title, fromUser, text, part.PromptIndex, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        bool added = BookmarkStore.Toggle(bookmark);
        Log.Write($"Bookmark {(added ? "added" : "removed")}: '{s.Title}', {(fromUser ? "you" : "Claude")}, prompt {part.PromptIndex + 1}");
        return added ? "★ Bookmarked" : "Bookmark removed";
    }
}

/// <summary>Bookmark menus (tray, outline button) and opening a bookmark.</summary>
static class BookmarkMenu
{
    public static void AddItems(ToolStripItemCollection items)
    {
        var all = BookmarkStore.Load();
        if (all.Count == 0)
        {
            items.Add(new ToolStripMenuItem("No bookmarks: point at a message in Claude and press Ctrl+D") { Enabled = false });
            return;
        }
        foreach (var b in all)
            items.Add(new ToolStripMenuItem(Label(b), null, (_, _) => Open(b)) { ToolTipText = b.Text });

        var remove = new ToolStripMenuItem("Remove a bookmark");
        foreach (var b in all)
            remove.DropDownItems.Add(new ToolStripMenuItem(Label(b), null, (_, _) => BookmarkStore.Remove(b)));
        items.Add(new ToolStripSeparator());
        items.Add(remove);
    }

    static string Label(Bookmark b)
    {
        var line = ConversationView.Normalize(b.Text.Split('\n')[0]);
        if (line.Length > 70) line = line[..69] + "…";
        return $"★  {b.SessionTitle}  ·  {(b.FromUser ? "You" : "Claude")}:  {line}".Replace("&", "&&");
    }

    /// <summary>Opens the session and, once it is on screen, scrolls to the message.</summary>
    public static void Open(Bookmark b)
    {
        Log.Write($"Bookmark: opening '{b.SessionTitle}' at prompt {b.PromptIndex + 1}");
        ClaudeLinks.Open($"claude://claude.ai/epitaxy/{b.SessionId}");
        if (b.PromptIndex < 0) return;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                long until = Environment.TickCount64 + 8000;
                while (Environment.TickCount64 < until && RecentSessions.OnScreenCliSessionId() != b.CliSessionId) Thread.Sleep(200);
                if (RecentSessions.OnScreenCliSessionId() != b.CliSessionId) return;
                Thread.Sleep(1000);
                var transcript = ConversationPrompts.FindTranscript(b.CliSessionId);
                if (transcript == null) return;
                var claude = ClaudeWindow.Find();
                OutlineButton.JumpToPrompt(claude, ConversationPrompts.Read(transcript), b.PromptIndex);
                if (b.FromUser) return;

                // Claude's reply: from its prompt, on to the reply's own heading.
                Thread.Sleep(400);
                var start = ConversationView.Normalize(b.Text);
                var area = ConversationView.ScrollArea(AutomationElement.FromHandle(claude));
                var reply = ConversationView.MessageHeadings(AutomationElement.FromHandle(claude))
                    .Where(h => !ConversationView.IsPrompt(h) && ConversationView.Matches(start, ConversationView.Normalize(ConversationView.MessageStart(h))))
                    .FirstOrDefault(h => area == null || h.Current.BoundingRectangle.Top >= area.Value.Top - 2);
                if (reply != null) ConversationView.ScrollTo(reply);
            }
            catch (Exception ex) { Log.Write($"Bookmark jump failed: {ex.GetType().Name}: {ex.Message}"); }
        });
    }
}

/// <summary>A short note by the mouse that fades after a moment; never takes focus.</summary>
sealed class FlashTip : Form
{
    const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8, WS_EX_TRANSPARENT = 0x20;
    readonly System.Windows.Forms.Timer _hide = new() { Interval = 1300 };
    string _text = "";

    public FlashTip()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.FromArgb(217, 119, 87);
        _hide.Tick += (_, _) => { _hide.Stop(); Hide(); };
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST | WS_EX_TRANSPARENT;
            return cp;
        }
    }

    public void Show(string text, Point at)
    {
        _text = text;
        float scale = DeviceDpi / 96f;
        using var font = new Font("Segoe UI", 14 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        var size = TextRenderer.MeasureText(text, font);
        int pad = (int)(10 * scale);
        SetWindowPos(Handle, new IntPtr(-1), at.X + pad, at.Y + pad, size.Width + pad * 2, size.Height + pad, 0x10);
        Invalidate();
        if (!Visible) Show();
        _hide.Stop();
        _hide.Start();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        float scale = DeviceDpi / 96f;
        using var font = new Font("Segoe UI", 14 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        TextRenderer.DrawText(e.Graphics, _text, font, ClientRectangle, Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
    }

    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
