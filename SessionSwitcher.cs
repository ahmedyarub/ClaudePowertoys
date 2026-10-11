using System.Diagnostics;
using System.Runtime.InteropServices;
using static ClaudePowertoys.KeyboardHook;

namespace ClaudePowertoys;

/// <summary>
/// Alt+Tab for Claude Desktop's recently active Code sessions. While Claude is in front, Ctrl+Tab
/// opens a list of them (most recently focused first) with the previous session selected;
/// Tab / Shift+Tab move the selection while Ctrl stays down, Esc cancels, and releasing Ctrl
/// switches to the selection. Tab is swallowed while Ctrl is held so the app's own Ctrl+Tab
/// (Go > Next Session) doesn't fire as well.
/// </summary>
sealed class SessionSwitcher : IDisposable
{
    // A scan started when Ctrl goes down is usually done before Tab; don't rescan more often than this.
    const int RescanMs = 1000;

    readonly KeyboardHook _hook;
    readonly SwitcherForm _form = new();
    readonly object _gate = new();

    List<SessionInfo> _sessions = [];
    long _scannedAt = -RescanMs;
    bool _scanning;

    // Switcher state, touched only on the UI thread (the hook runs there too).
    List<SessionInfo>? _shown;
    int _selected;

    public volatile bool Enabled;
    public TimeSpan RecentWindow = TimeSpan.FromHours(8);

    public SessionSwitcher()
    {
        _ = _form.Handle;
        _hook = new KeyboardHook("Session switcher", (message, vk) => Enabled && Handle(message, vk));
    }

    public void Dispose()
    {
        _hook.Dispose();
        _form.Dispose();
    }

    /// <summary>Returns true to swallow the key.</summary>
    bool Handle(int message, int vk)
    {
        bool down = IsDown(message);

        if (_shown != null)
        {
            if (vk == VK_TAB)
            {
                if (down) Move(KeyHeld(VK_SHIFT) ? -1 : 1);
                return true;
            }
            if (vk == VK_ESCAPE)
            {
                if (down) Close(switchTo: null);
                return true;
            }
            if (IsUp(message) && vk is VK_LCONTROL or VK_RCONTROL or VK_CONTROL)
            {
                Close(switchTo: _shown.Count > 0 ? _shown[_selected] : null);
                return false;
            }
            return false;
        }

        if (!down) return false;
        if (vk is VK_LCONTROL or VK_RCONTROL or VK_CONTROL)
        {
            if (ClaudeWindow.IsInFront()) ScanInBackground();
            return false;
        }
        if (vk == VK_TAB && KeyHeld(VK_CONTROL) && !KeyHeld(VK_MENU) && !KeyHeld(VK_LWIN) && !KeyHeld(VK_RWIN) && ClaudeWindow.IsInFront())
        {
            Open(backwards: KeyHeld(VK_SHIFT));
            return true;
        }
        return false;
    }

    void Open(bool backwards)
    {
        // Empty only if Tab beat the first scan; ScanInBackground's result then fills the list in.
        lock (_gate) _shown = _sessions;
        _selected = _shown.Count < 2 ? 0 : backwards ? _shown.Count - 1 : 1;
        var shown = _shown;
        int selected = _selected;
        var claude = ClaudeWindow.Find();
        _form.BeginInvoke(() => _form.ShowList(claude, shown, selected));
    }

    void Move(int step)
    {
        if (_shown == null || _shown.Count == 0) return;
        _selected = (_selected + step + _shown.Count) % _shown.Count;
        int selected = _selected;
        _form.BeginInvoke(() => _form.Select(selected));
    }

    void Close(SessionInfo? switchTo)
    {
        bool isCurrent = _shown != null && _shown.Count > 0 && switchTo == _shown[0];
        _shown = null;
        _form.BeginInvoke(() => _form.Conceal());
        if (switchTo == null || isCurrent) return;

        Log.Write($"Switch to session '{switchTo.Title}' ({switchTo.SessionId})");
        // Off the hook: ShellExecute can take long enough for Windows to drop a low-level hook.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Process.Start(new ProcessStartInfo(switchTo.Link) { UseShellExecute = true })?.Dispose(); }
            catch (Exception ex) { Log.Write($"Opening {switchTo.Link} failed: {ex.Message}"); }
        });
    }

    void ScanInBackground()
    {
        lock (_gate)
        {
            if (_scanning || Environment.TickCount64 - _scannedAt < RescanMs) return;
            _scanning = true;
        }
        ThreadPool.QueueUserWorkItem(_ =>
        {
            List<SessionInfo>? found = null;
            try { found = RecentSessions.Scan(RecentWindow); }
            catch (Exception ex) { Log.Write($"Session scan failed: {ex.GetType().Name}: {ex.Message}"); }
            lock (_gate)
            {
                if (found != null) _sessions = found;
                _scannedAt = Environment.TickCount64;
                _scanning = false;
            }
            // A switcher opened before the first scan finished gets the list now.
            if (found != null) _form.BeginInvoke(() => Refill(found));
        });
    }

    void Refill(List<SessionInfo> found)
    {
        if (_shown == null || _shown.Count > 0) return;
        _shown = found;
        _selected = found.Count < 2 ? 0 : 1;
        _form.ShowList(ClaudeWindow.Find(), found, _selected);
    }
}

/// <summary>The session list: topmost, never activated, drawn by hand so it scales with the monitor's DPI.</summary>
sealed class SwitcherForm : Form
{
    const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    const uint SWP_NOACTIVATE = 0x10;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    // Logical pixels, scaled by the DPI of Claude's monitor.
    const int WidthLogical = 560, RowLogical = 44, PadLogical = 12, HeaderLogical = 34;

    static readonly Color Background = Color.FromArgb(38, 38, 36);
    static readonly Color Border = Color.FromArgb(70, 70, 66);
    static readonly Color Highlight = Color.FromArgb(217, 119, 87);
    static readonly Color TitleText = Color.FromArgb(240, 238, 230);
    static readonly Color DimText = Color.FromArgb(160, 158, 150);

    List<SessionInfo> _sessions = [];
    int _selected;
    float _scale = 1;

    public SwitcherForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Background;
        DoubleBuffered = true;
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST;
            return cp;
        }
    }

    public void ShowList(IntPtr claude, List<SessionInfo> sessions, int selected)
    {
        _sessions = sessions;
        _selected = selected;
        _scale = (claude != IntPtr.Zero ? GetDpiForWindow(claude) : 96) / 96f;

        int rows = Math.Max(sessions.Count, 1);
        int width = S(WidthLogical);
        int height = S(HeaderLogical) + rows * S(RowLogical) + S(PadLogical) * 2;
        var area = claude != IntPtr.Zero ? Native.VisibleBounds(claude) : default;
        var screen = Screen.FromHandle(claude != IntPtr.Zero ? claude : Handle).WorkingArea;
        int cx = area.Right > area.Left ? (area.Left + area.Right) / 2 : (screen.Left + screen.Right) / 2;
        int cy = area.Bottom > area.Top ? (area.Top + area.Bottom) / 2 : (screen.Top + screen.Bottom) / 2;
        height = Math.Min(height, screen.Height - S(40));

        SetWindowPos(Handle, HWND_TOPMOST, cx - width / 2, cy - height / 2, width, height, SWP_NOACTIVATE);
        Invalidate();
        if (!Visible) Show();
    }

    public void Select(int selected)
    {
        _selected = selected;
        Invalidate();
    }

    public void Conceal()
    {
        if (Visible) Hide();
    }

    int S(int logical) => (int)Math.Round(logical * _scale);

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
        using (var border = new Pen(Border, Math.Max(1, S(1))))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        using var titleFont = new Font("Segoe UI", 15 * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var dimFont = new Font("Segoe UI", 12 * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
        int pad = S(PadLogical), row = S(RowLogical);

        TextRenderer.DrawText(g, "Recent sessions", dimFont, new Rectangle(pad, pad, Width - pad * 2, S(HeaderLogical) - pad / 2),
            DimText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);

        int y = pad + S(HeaderLogical) - pad / 2;
        if (_sessions.Count == 0)
        {
            TextRenderer.DrawText(g, "Looking for recent sessions…", titleFont, new Rectangle(pad, y, Width - pad * 2, row),
                DimText, TextFormatFlags.Left | TextFormatFlags.VerticalCenter);
            return;
        }

        for (int i = 0; i < _sessions.Count && y + row <= Height; i++, y += row)
        {
            var s = _sessions[i];
            var rect = new Rectangle(pad / 2, y, Width - pad, row);
            if (i == _selected)
            {
                using var fill = new SolidBrush(Color.FromArgb(70, Highlight));
                using var edge = new Pen(Highlight, Math.Max(1, S(2)));
                g.FillRectangle(fill, rect);
                g.DrawRectangle(edge, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            }
            int folderWidth = s.Folder == "" ? 0 : Math.Min(S(170), TextRenderer.MeasureText(s.Folder, dimFont).Width + pad);
            var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix;
            TextRenderer.DrawText(g, s.Title + (i == 0 ? "  (current)" : ""), titleFont,
                new Rectangle(rect.X + pad, y, rect.Width - pad * 2 - folderWidth, row), TitleText, flags);
            if (folderWidth > 0)
                TextRenderer.DrawText(g, s.Folder, dimFont, new Rectangle(rect.Right - pad - folderWidth, y, folderWidth, row),
                    DimText, (flags & ~TextFormatFlags.Left) | TextFormatFlags.Right);
        }
    }

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
