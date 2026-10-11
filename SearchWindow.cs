using System.Runtime.InteropServices;

namespace ClaudePowertoys;

/// <summary>
/// Search across every session: type to find prompts and Claude's replies; Enter (or a double-click)
/// opens the session and scrolls to the prompt the match belongs to. Up / Down move through results
/// from the search box, Esc closes.
/// </summary>
sealed class SearchWindow : Form
{
    static readonly Color Background = Color.FromArgb(38, 38, 36);
    static readonly Color Field = Color.FromArgb(52, 52, 49);
    static readonly Color Highlight = Color.FromArgb(217, 119, 87);
    static readonly Color RowText = Color.FromArgb(240, 238, 230);
    static readonly Color DimText = Color.FromArgb(160, 158, 150);

    // Logical pixels.
    const int WidthLogical = 900, HeightLogical = 620, RowLogical = 58, SnippetChars = 160;

    readonly TextBox _query = new();
    readonly ListBox _results = new();
    readonly Label _status = new();
    readonly System.Windows.Forms.Timer _debounce = new() { Interval = 200 };
    List<SearchHit> _hits = [];
    int _searchId;

    public SearchWindow()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        TopMost = true;
        BackColor = Background;
        KeyPreview = true;

        _query.BorderStyle = BorderStyle.None;
        _query.BackColor = Field;
        _query.ForeColor = RowText;
        _query.PlaceholderText = "Search all sessions: your prompts and Claude's replies";
        _query.TextChanged += (_, _) => { _debounce.Stop(); _debounce.Start(); };
        _query.KeyDown += OnQueryKey;

        _status.ForeColor = DimText;
        _status.BackColor = Background;

        _results.BorderStyle = BorderStyle.None;
        _results.BackColor = Background;
        _results.DrawMode = DrawMode.OwnerDrawFixed;
        _results.IntegralHeight = false;
        _results.DrawItem += DrawHit;
        _results.DoubleClick += (_, _) => OpenSelected();
        _results.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; } };

        _debounce.Tick += (_, _) => { _debounce.Stop(); RunSearch(); };
        Controls.AddRange([_query, _status, _results]);
        Deactivate += (_, _) => Hide();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape) { Hide(); e.Handled = true; return; }
        base.OnKeyDown(e);
    }

    /// <summary>Centered on Claude, sized for its monitor's DPI; keeps the last query, selected.</summary>
    public void Open(IntPtr claude)
    {
        float scale = (claude != IntPtr.Zero ? GetDpiForWindow(claude) : 96) / 96f;
        int S(int v) => (int)Math.Round(v * scale);
        var area = claude != IntPtr.Zero ? Native.VisibleBounds(claude) : default;
        var screen = Screen.FromHandle(claude != IntPtr.Zero ? claude : Handle).WorkingArea;
        int width = Math.Min(S(WidthLogical), screen.Width - S(40)), height = Math.Min(S(HeightLogical), screen.Height - S(40));
        int cx = area.Right > area.Left ? (area.Left + area.Right) / 2 : (screen.Left + screen.Right) / 2;
        int cy = area.Bottom > area.Top ? (area.Top + area.Bottom) / 2 : (screen.Top + screen.Bottom) / 2;
        Bounds = new Rectangle(cx - width / 2, cy - height / 2, width, height);

        int pad = S(14);
        _query.Font = new Font("Segoe UI", 16 * scale, GraphicsUnit.Pixel);
        _query.SetBounds(pad, pad, width - pad * 2, S(28));
        _status.Font = new Font("Segoe UI", 12 * scale, GraphicsUnit.Pixel);
        _status.SetBounds(pad, _query.Bottom + S(6), width - pad * 2, S(20));
        _results.Font = new Font("Segoe UI", 14 * scale, GraphicsUnit.Pixel);
        _results.ItemHeight = Math.Min(255, S(RowLogical));
        _results.SetBounds(pad / 2, _status.Bottom + S(6), width - pad, height - _status.Bottom - S(6) - pad / 2);

        Show();
        Activate();
        _query.Focus();
        _query.SelectAll();
        if (_query.Text != "") RunSearch();
        else _status.Text = "Type to search. Enter opens, Esc closes.";
    }

    void OnQueryKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyCode is Keys.Down or Keys.Up && _results.Items.Count > 0)
        {
            int next = _results.SelectedIndex + (e.KeyCode == Keys.Down ? 1 : -1);
            _results.SelectedIndex = Math.Clamp(next, 0, _results.Items.Count - 1);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            _debounce.Stop();
            if (_results.SelectedIndex < 0) RunSearch();
            OpenSelected();
            e.Handled = e.SuppressKeyPress = true;
        }
    }

    /// <summary>Searches off the UI thread; a newer query makes an older result stale.</summary>
    void RunSearch()
    {
        var query = _query.Text.Trim();
        int id = ++_searchId;
        _status.Text = query == "" ? "Type to search. Enter opens, Esc closes." : "Searching…";
        if (query == "") { Show([], query); return; }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            List<SearchHit> hits;
            try { hits = SearchIndex.Search(query); }
            catch (Exception ex) { Log.Write($"Search failed: {ex.Message}"); hits = []; }
            BeginInvoke(() => { if (id == _searchId) Show(hits, query); });
        });
    }

    void Show(List<SearchHit> hits, string query)
    {
        _hits = hits;
        _results.BeginUpdate();
        _results.Items.Clear();
        foreach (var h in hits) _results.Items.Add(h);
        _results.EndUpdate();
        _results.Tag = query;
        if (hits.Count > 0) _results.SelectedIndex = 0;
        if (query != "") _status.Text = hits.Count == 0 ? "No matches" : $"{hits.Count}{(hits.Count >= 200 ? "+" : "")} matches, newest first";
    }

    void DrawHit(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _hits.Count) return;
        var h = _hits[e.Index];
        bool selected = (e.State & DrawItemState.Selected) != 0;
        using (var back = new SolidBrush(selected ? Color.FromArgb(70, Highlight) : Background)) e.Graphics.FillRectangle(back, e.Bounds);

        int pad = e.Bounds.Height / 6, half = e.Bounds.Height / 2;
        using var small = new Font(_results.Font.FontFamily, _results.Font.Size * 0.85f, GraphicsUnit.Pixel);
        var query = _results.Tag as string ?? "";
        var terms = SearchIndex.Terms(query);
        var when = DateTimeOffset.FromUnixTimeMilliseconds(h.LastActivityAt).LocalDateTime.ToString("MMM d");
        var top = new RectangleF(e.Bounds.X + pad, e.Bounds.Y, e.Bounds.Width - pad * 2, half);
        var bottom = new RectangleF(e.Bounds.X + pad, e.Bounds.Y + half - pad / 2f, e.Bounds.Width - pad * 2, half);
        var archived = h.IsArchived ? "  ·  archived" : "";

        if (h.InTitle)
        {
            // A title match: the title itself is the result.
            DrawHighlighted(e.Graphics, $"Session title  ·  {h.Session.Folder}  ·  {when}{archived}", small, top, DimText, []);
            DrawHighlighted(e.Graphics, h.Session.Title, _results.Font, bottom, RowText, terms);
            return;
        }
        DrawHighlighted(e.Graphics, $"{h.Session.Title}  ·  {(h.FromUser ? "You" : "Claude")}  ·  {h.Session.Folder}  ·  {when}{archived}", small, top, DimText, terms);
        DrawHighlighted(e.Graphics, SearchIndex.Snippet(h.Text, query, SnippetChars), _results.Font, bottom, RowText, terms);
    }

    static readonly StringFormat Measure = new(StringFormat.GenericTypographic)
    {
        FormatFlags = StringFormatFlags.MeasureTrailingSpaces | StringFormatFlags.NoWrap,
        LineAlignment = StringAlignment.Center,
    };

    /// <summary>One line of text, every occurrence of a term on a highlight; cut with "…" at the right edge.</summary>
    static void DrawHighlighted(Graphics g, string text, Font font, RectangleF rect, Color color, string[] terms)
    {
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        // Which characters fall inside a match of any term.
        var hit = new bool[text.Length];
        foreach (var term in terms)
            for (int at = text.IndexOf(term, StringComparison.OrdinalIgnoreCase); at >= 0; at = text.IndexOf(term, at + 1, StringComparison.OrdinalIgnoreCase))
                for (int k = at; k < at + term.Length; k++) hit[k] = true;

        using var plain = new SolidBrush(color);
        using var marked = new SolidBrush(Color.White);
        using var mark = new SolidBrush(Color.FromArgb(150, Highlight));
        float x = rect.X, ellipsis = g.MeasureString("…", font, PointF.Empty, Measure).Width;
        for (int start = 0; start < text.Length;)
        {
            int end = start;
            while (end < text.Length && hit[end] == hit[start]) end++;
            var run = text[start..end];
            float width = g.MeasureString(run, font, PointF.Empty, Measure).Width;
            bool last = x + width > rect.Right;
            if (last)
            {
                // Shorten the run to what fits before the ellipsis.
                while (run.Length > 0 && x + g.MeasureString(run, font, PointF.Empty, Measure).Width > rect.Right - ellipsis) run = run[..^1];
                width = g.MeasureString(run, font, PointF.Empty, Measure).Width;
            }
            var box = new RectangleF(x, rect.Y, width, rect.Height);
            if (hit[start]) g.FillRectangle(mark, x, rect.Y + rect.Height * 0.15f, width, rect.Height * 0.7f);
            g.DrawString(run, font, hit[start] ? marked : plain, box, Measure);
            x += width;
            if (last)
            {
                g.DrawString("…", font, plain, new RectangleF(x, rect.Y, ellipsis + 2, rect.Height), Measure);
                return;
            }
            start = end;
        }
    }

    void OpenSelected()
    {
        if (_results.SelectedIndex < 0 || _results.SelectedIndex >= _hits.Count) return;
        var hit = _hits[_results.SelectedIndex];
        Hide();
        Log.Write(hit.InTitle ? $"Search: opening '{hit.Session.Title}' (title match)" : $"Search: opening '{hit.Session.Title}' at prompt {hit.PromptIndex + 1}");
        ClaudeLinks.Open(hit.Session.Link);
        if (hit.PromptIndex < 0) return;
        ThreadPool.QueueUserWorkItem(_ => JumpWhenOpen(hit));
    }

    /// <summary>Waits for the session to come on screen, then scrolls to the prompt.</summary>
    static void JumpWhenOpen(SearchHit hit)
    {
        try
        {
            long until = Environment.TickCount64 + 8000;
            while (Environment.TickCount64 < until && RecentSessions.OnScreenCliSessionId() != hit.Session.CliSessionId) Thread.Sleep(200);
            if (RecentSessions.OnScreenCliSessionId() != hit.Session.CliSessionId)
            {
                Log.Write($"Search: '{hit.Session.Title}' didn't come on screen; not scrolling");
                return;
            }
            Thread.Sleep(1000);
            var transcript = ConversationPrompts.FindTranscript(hit.Session.CliSessionId);
            if (transcript == null) return;
            OutlineButton.JumpToPrompt(ClaudeWindow.Find(), ConversationPrompts.Read(transcript), hit.PromptIndex);
        }
        catch (Exception ex) { Log.Write($"Search jump failed: {ex.GetType().Name}: {ex.Message}"); }
    }

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
}

/// <summary>Calls back on the UI thread whenever another window comes to the foreground.</summary>
sealed class ForegroundWatcher : IDisposable
{
    const uint EVENT_SYSTEM_FOREGROUND = 3, WINEVENT_OUTOFCONTEXT = 0;

    readonly WinEventProc _proc;
    readonly IntPtr _hook;

    public ForegroundWatcher(Action<IntPtr> onForeground)
    {
        _proc = (_, _, hwnd, _, _, _, _) => onForeground(hwnd);
        _hook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND, IntPtr.Zero, _proc, 0, 0, WINEVENT_OUTOFCONTEXT);
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWinEvent(_hook);
    }

    delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int obj, int child, uint thread, uint time);

    [DllImport("user32.dll")] static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
    [DllImport("user32.dll")] static extern bool UnhookWinEvent(IntPtr hook);
}
