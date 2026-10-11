using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using static ClaudePowertoys.KeyboardHook;

namespace ClaudePowertoys;

/// <param name="Text">The message's text (its start, for a long one), as the transcript has it.</param>
/// <param name="PromptIndex">The prompt the message is, or the prompt Claude's reply answers; for scrolling back to it.</param>
/// <param name="Name">What the bookmark is called; the message's first line until renamed.</param>
/// <param name="Folder">The folder it is filed in; "" is Unsorted.</param>
sealed record Bookmark(string SessionId, string CliSessionId, string SessionTitle, bool FromUser, string Text, int PromptIndex, long AddedAt,
    string Id = "", string Name = "", string Folder = "")
{
    public static string DefaultName(string text)
    {
        var line = ConversationView.Normalize(text.Split('\n')[0]);
        return line.Length <= 90 ? line : line[..89] + "…";
    }
}

/// <summary>
/// Bookmarked messages and their folders, in %APPDATA%\ClaudePowertoys\bookmarks.json. Bookmarks are
/// kept newest first. Every change raises Changed, so an open manager can refresh.
/// </summary>
static class BookmarkStore
{
    sealed class Data
    {
        public List<string> Folders { get; set; } = [];
        public List<Bookmark> Bookmarks { get; set; } = [];
    }

    static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudePowertoys", "bookmarks.json");
    static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    static readonly object Gate = new();

    /// <summary>Raised after every change, on the thread that made it.</summary>
    public static event Action? Changed;

    public static List<Bookmark> Load() => Read().Bookmarks;

    public static List<string> Folders() => Read().Folders;

    /// <summary>Adds the bookmark (to Unsorted), or removes it when that message is already bookmarked. Returns whether it was added.</summary>
    public static bool Toggle(Bookmark bookmark)
    {
        bool added = false;
        Change(d =>
        {
            int existing = d.Bookmarks.FindIndex(b => Same(b, bookmark));
            if (existing >= 0) d.Bookmarks.RemoveAt(existing);
            else d.Bookmarks.Insert(0, bookmark with { Id = Guid.NewGuid().ToString("N"), Name = Bookmark.DefaultName(bookmark.Text) });
            added = existing < 0;
        });
        return added;
    }

    public static void Remove(IEnumerable<string> ids)
    {
        var set = ids.ToHashSet();
        Change(d => d.Bookmarks.RemoveAll(b => set.Contains(b.Id)));
    }

    public static void Rename(string id, string name) =>
        Change(d => Replace(d, id, b => b with { Name = name.Trim() == "" ? Bookmark.DefaultName(b.Text) : name.Trim() }));

    public static void Move(IEnumerable<string> ids, string folder)
    {
        var set = ids.ToHashSet();
        Change(d =>
        {
            for (int i = 0; i < d.Bookmarks.Count; i++)
                if (set.Contains(d.Bookmarks[i].Id)) d.Bookmarks[i] = d.Bookmarks[i] with { Folder = folder };
        });
    }

    /// <summary>Adds a folder named <paramref name="name"/>, numbered if taken; returns the name used.</summary>
    public static string AddFolder(string name)
    {
        string used = name;
        Change(d =>
        {
            for (int n = 2; d.Folders.Contains(used, StringComparer.OrdinalIgnoreCase); n++) used = $"{name} {n}";
            d.Folders.Add(used);
        });
        return used;
    }

    /// <summary>Renames a folder; false when another folder already has the name.</summary>
    public static bool RenameFolder(string from, string to)
    {
        to = to.Trim();
        bool done = false;
        Change(d =>
        {
            if (to == "" || d.Folders.Any(f => f.Equals(to, StringComparison.OrdinalIgnoreCase) && f != from)) return;
            d.Folders[d.Folders.IndexOf(from)] = to;
            for (int i = 0; i < d.Bookmarks.Count; i++)
                if (d.Bookmarks[i].Folder == from) d.Bookmarks[i] = d.Bookmarks[i] with { Folder = to };
            done = true;
        });
        return done;
    }

    /// <summary>Deletes a folder; its bookmarks move to Unsorted rather than being deleted.</summary>
    public static void DeleteFolder(string folder) => Change(d =>
    {
        d.Folders.Remove(folder);
        for (int i = 0; i < d.Bookmarks.Count; i++)
            if (d.Bookmarks[i].Folder == folder) d.Bookmarks[i] = d.Bookmarks[i] with { Folder = "" };
    });

    static void Replace(Data d, string id, Func<Bookmark, Bookmark> change)
    {
        int i = d.Bookmarks.FindIndex(b => b.Id == id);
        if (i >= 0) d.Bookmarks[i] = change(d.Bookmarks[i]);
    }

    static bool Same(Bookmark a, Bookmark b) =>
        a.CliSessionId == b.CliSessionId && a.FromUser == b.FromUser && a.PromptIndex == b.PromptIndex && a.Text == b.Text;

    static void Change(Action<Data> change)
    {
        lock (Gate)
        {
            var d = Read();
            change(d);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            // The first write in the folders format keeps the old plain list beside it.
            var backup = Path.ChangeExtension(FilePath, ".v1.json");
            if (File.Exists(FilePath) && !File.Exists(backup) && File.ReadAllText(FilePath).TrimStart().StartsWith('['))
                File.Copy(FilePath, backup);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(d, Json));
        }
        Changed?.Invoke();
    }

    /// <summary>Reads the file; the first version was a plain list of bookmarks without ids, names or folders.</summary>
    static Data Read()
    {
        lock (Gate)
        {
            if (!File.Exists(FilePath)) return new Data();
            try
            {
                var text = File.ReadAllText(FilePath);
                var d = text.TrimStart().StartsWith('[')
                    ? new Data { Bookmarks = JsonSerializer.Deserialize<List<Bookmark>>(text) ?? [] }
                    : JsonSerializer.Deserialize<Data>(text) ?? new Data();
                for (int i = 0; i < d.Bookmarks.Count; i++)
                {
                    var b = d.Bookmarks[i];
                    if (b.Id == "" || b.Name == "" || b.Folder == null)
                        d.Bookmarks[i] = b with
                        {
                            Id = b.Id == "" ? Guid.NewGuid().ToString("N") : b.Id,
                            Name = b.Name == "" ? Bookmark.DefaultName(b.Text) : b.Name,
                            Folder = b.Folder ?? "",
                        };
                }
                return d;
            }
            catch (JsonException ex)
            {
                Log.Write($"bookmarks.json is not valid JSON: {ex.Message}");
                return new Data();
            }
        }
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
    /// <summary>Opens the bookmark manager; set by the tray, which owns it.</summary>
    public static Action? OpenManager;

    /// <summary>Folders as submenus, then Unsorted bookmarks, then Manage bookmarks….</summary>
    public static void AddItems(ToolStripItemCollection items)
    {
        var all = BookmarkStore.Load();
        foreach (var folder in BookmarkStore.Folders())
        {
            var sub = new ToolStripMenuItem($"📁  {folder}".Replace("&", "&&"));
            foreach (var b in all.Where(b => b.Folder == folder)) sub.DropDownItems.Add(Item(b));
            if (sub.DropDownItems.Count == 0) sub.DropDownItems.Add(new ToolStripMenuItem("(empty)") { Enabled = false });
            items.Add(sub);
        }
        foreach (var b in all.Where(b => b.Folder == "")) items.Add(Item(b));
        if (all.Count == 0)
            items.Add(new ToolStripMenuItem("No bookmarks: point at a message in Claude and press Ctrl+D") { Enabled = false });
        items.Add(new ToolStripSeparator());
        items.Add(new ToolStripMenuItem("Manage bookmarks…", null, (_, _) => OpenManager?.Invoke()));
    }

    static ToolStripMenuItem Item(Bookmark b) =>
        new($"★  {b.Name}  ·  {b.SessionTitle}".Replace("&", "&&"), null, (_, _) => Open(b)) { ToolTipText = b.Text };

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
