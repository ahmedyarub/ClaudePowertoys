using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace ClaudePowertoys;

/// <summary>
/// A floating table of contents for the conversation on screen: a small button just right of the
/// conversation column lists the prompts typed in the session (from its transcript file); picking
/// one scrolls the conversation to it. The app only keeps part of a long conversation loaded, so
/// when the prompt isn't loaded, its "Load earlier messages" / "Load later messages" buttons are
/// pressed until it is. A second button below it starts a Claude Code session on Venice AI.
/// </summary>
sealed class OutlineButton : IDisposable
{
    const int PollMs = 300;
    const int SizeLogical = 32, GapLogical = 12, TopLogical = 64;
    const int MaxLoadPresses = 30;

    static readonly Condition PromptBoxCondition = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
        new PropertyCondition(AutomationElement.NameProperty, "Prompt"));

    readonly ButtonWindow _button;
    readonly ButtonWindow _veniceButton;
    readonly Action<ToolStripItemCollection> _addVeniceItems;
    readonly Thread _thread;
    volatile bool _stop;
    IntPtr _hwnd;
    AutomationElement? _promptBox;
    int _jumping;

    public volatile bool Enabled;
    public volatile bool VeniceEnabled;

    /// <param name="addVeniceItems">Fills the Venice button's menu.</param>
    public OutlineButton(Action<ToolStripItemCollection> addVeniceItems)
    {
        _addVeniceItems = addVeniceItems;
        _button = new ButtonWindow(ShowOutline, ShowExportMenu, ButtonWindow.DrawList,
            "Conversation outline", "Conversation outline (right-click to copy or export)");
        _veniceButton = new ButtonWindow(ShowVeniceMenu, ShowVeniceMenu, ButtonWindow.DrawV,
            "New Venice AI session", "New Claude Code session on Venice AI");
        _ = _button.Handle;
        _ = _veniceButton.Handle;
        _thread = new Thread(Loop) { IsBackground = true, Name = "OutlineButton" };
    }

    public void Start() => _thread.Start();

    public void Dispose()
    {
        _stop = true;
        _button.Dispose();
        _veniceButton.Dispose();
    }

    void Loop()
    {
        while (!_stop)
        {
            Thread.Sleep(PollMs);
            try
            {
                if ((Enabled || VeniceEnabled) && Tick()) continue;
            }
            catch (ElementNotAvailableException)
            {
                _promptBox = null;
            }
            catch (Exception ex)
            {
                Log.Write($"Outline button failed: {ex.GetType().Name}: {ex.Message}");
                _promptBox = null;
                Thread.Sleep(1000);
            }
            Post(() => { _button.Conceal(); _veniceButton.Conceal(); });
        }
    }

    /// <summary>Places the buttons beside the conversation column; false when there is no conversation on screen.</summary>
    bool Tick()
    {
        if (_hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd))
        {
            _hwnd = ClaudeWindow.Find();
            _promptBox = null;
            if (_hwnd == IntPtr.Zero) return false;
        }
        if (Native.IsIconic(_hwnd)) return false;

        _promptBox ??= AutomationElement.FromHandle(_hwnd).FindFirst(TreeScope.Descendants, PromptBoxCondition);
        if (_promptBox == null) return false;
        var box = _promptBox.Current.BoundingRectangle;
        if (box.IsEmpty || box.Width <= 0) return false;

        float scale = GetDpiForWindow(_hwnd) / 96f;
        int size = (int)(SizeLogical * scale), gap = (int)(GapLogical * scale);
        var window = Native.VisibleBounds(_hwnd);
        int x = (int)box.Right + gap;
        if (x + size > window.Right - gap) x = (int)box.Right - size;
        var bounds = new Rectangle(x, window.Top + (int)(TopLogical * scale), size, size);

        // The Venice button sits below the outline button, or in its place when that one is off.
        var veniceBounds = Enabled ? bounds with { Y = bounds.Bottom + gap / 2 } : bounds;
        bool outline = Enabled, venice = VeniceEnabled;
        var owner = _hwnd;
        Post(() =>
        {
            if (outline) _button.Place(owner, bounds); else _button.Conceal();
            if (venice) _veniceButton.Place(owner, veniceBounds); else _veniceButton.Conceal();
        });
        return true;
    }

    void Post(Action action)
    {
        if (_stop || _button.IsDisposed) return;
        try { _button.BeginInvoke(action); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>Runs on the UI thread when the button is clicked.</summary>
    void ShowOutline()
    {
        var cli = RecentSessions.OnScreenCliSessionId();
        var transcript = cli == null ? null : ConversationPrompts.FindTranscript(cli);
        var prompts = transcript == null ? [] : ConversationPrompts.Read(transcript);

        var menu = new ContextMenuStrip { ShowImageMargin = false, Renderer = DarkMenu.Renderer };
        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose);
        if (prompts.Count == 0)
        {
            menu.Items.Add(new ToolStripMenuItem("No prompts found for this session") { Enabled = false });
            Log.Write($"Outline: no prompts (session {cli ?? "unknown"}, transcript {transcript ?? "not found"})");
        }
        for (int i = 0; i < prompts.Count; i++)
        {
            int index = i;
            var label = $"{i + 1}.  {FirstLine(prompts[i], 90)}";
            menu.Items.Add(new ToolStripMenuItem(label, null, (_, _) => JumpInBackground(prompts, index)) { ToolTipText = FirstLine(prompts[i], 400) });
        }
        menu.MaximumSize = new Size(Screen.FromControl(_button).WorkingArea.Width / 2, Screen.FromControl(_button).WorkingArea.Height * 3 / 4);
        menu.Show(_button, new Point(_button.Width, _button.Height), ToolStripDropDownDirection.BelowLeft);
    }

    /// <summary>Right-click: copy Claude's last answer, or export the conversation. Runs on the UI thread.</summary>
    void ShowExportMenu()
    {
        var session = RecentSessions.OnScreen();
        var transcript = session == null ? null : ConversationPrompts.FindTranscript(session.Value.CliSessionId);

        var menu = new ContextMenuStrip { ShowImageMargin = false, Renderer = DarkMenu.Renderer };
        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose);
        menu.Items.Add(new ToolStripMenuItem("Copy Claude's last answer as Markdown", null, (_, _) =>
        {
            var answer = ConversationExport.LastAnswer(transcript!);
            if (answer == null) { Log.Write("Copy last answer: no finished reply in this session"); return; }
            Clipboard.SetText(answer);
            Log.Write($"Copied the last answer ({answer.Length} characters)");
        }) { Enabled = transcript != null });
        menu.Items.Add(new ToolStripMenuItem("Export conversation to Markdown…", null, (_, _) => Export(transcript!, session!.Value.Title))
            { Enabled = transcript != null });
        menu.Items.Add(new ToolStripSeparator());
        var bookmarks = new ToolStripMenuItem("Bookmarks");
        BookmarkMenu.AddItems(bookmarks.DropDownItems);
        menu.Items.Add(bookmarks);
        menu.Show(_button, new Point(_button.Width, _button.Height), ToolStripDropDownDirection.BelowLeft);
    }

    /// <summary>Folders to start a Venice session in, the session on screen's first. Runs on the UI thread.</summary>
    void ShowVeniceMenu()
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false, Renderer = DarkMenu.Renderer };
        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose);
        menu.Items.Add(new ToolStripMenuItem("New Venice session in…") { Enabled = false });
        _addVeniceItems(menu.Items);
        menu.Show(_veniceButton, new Point(_veniceButton.Width, _veniceButton.Height), ToolStripDropDownDirection.BelowLeft);
    }

    static void Export(string transcript, string title)
    {
        var safeName = string.Concat(title.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '-' : c)).Trim();
        using var dialog = new SaveFileDialog
        {
            Title = "Export conversation",
            Filter = "Markdown (*.md)|*.md",
            FileName = (safeName.Length > 80 ? safeName[..80] : safeName) + ".md",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog() != DialogResult.OK) return;
        File.WriteAllText(dialog.FileName, ConversationExport.ToMarkdown(transcript, title));
        Log.Write($"Exported the conversation to {dialog.FileName}");
    }

    void JumpInBackground(List<string> prompts, int index)
    {
        if (Interlocked.CompareExchange(ref _jumping, 1, 0) != 0) return;
        var hwnd = _hwnd;
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { JumpToPrompt(hwnd, prompts, index); }
            catch (Exception ex) { Log.Write($"Outline jump failed: {ex.GetType().Name}: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _jumping, 0); }
        });
    }

    /// <summary>Scrolls the conversation on screen to prompt <paramref name="index"/> of <paramref name="prompts"/> (its transcript's prompts).</summary>
    public static void JumpToPrompt(IntPtr hwnd, List<string> prompts, int index)
    {
        var window = AutomationElement.FromHandle(hwnd);
        var key = Normalize(prompts[index]);
        // Prompts with the same opening ("2", "3") are told apart by their position among equals.
        int occurrence = prompts.Take(index).Count(p => Matches(Normalize(p), key));
        int total = prompts.Count(p => Matches(Normalize(p), key));

        foreach (var loadButton in new[] { ConversationView.LoadEarlier, ConversationView.LoadLater })
        {
            for (int presses = 0; presses <= MaxLoadPresses; presses++)
            {
                var headings = ConversationView.PromptHeadings(window)
                    .Where(h => Matches(key, Normalize(ConversationView.PromptStart(h))))
                    .ToList();
                if (headings.Count > 0)
                {
                    // All equals loaded: pick by position. Otherwise the target is the first or last loaded one.
                    var target = headings.Count == total ? headings[occurrence]
                        : occurrence == total - 1 ? headings[^1] : headings[0];
                    ConversationView.ScrollTo(target);
                    Log.Write($"Outline: jumped to prompt {index + 1} after {presses} load press(es)");
                    return;
                }
                if (!ConversationView.Press(window, loadButton)) break;
                Thread.Sleep(600);
            }
        }
        Log.Write($"Outline: prompt {index + 1} not found in the conversation ('{FirstLine(prompts[index], 60)}')");
    }

    static bool Matches(string text, string heading) => ConversationView.Matches(text, heading);

    static string Normalize(string text) => ConversationView.Normalize(text);

    static string FirstLine(string text, int max)
    {
        var line = Normalize(text.Split('\n')[0]);
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>A round button: drawn by hand, never takes focus.</summary>
    sealed class ButtonWindow : OwnedToolWindow
    {
        static readonly Color Fill = Color.FromArgb(48, 48, 46);
        static readonly Color FillHover = Color.FromArgb(70, 70, 66);
        static readonly Color Edge = Color.FromArgb(90, 90, 85);
        static readonly Color Glyph = Color.FromArgb(230, 228, 220);

        readonly Action _onClick;
        readonly Action _onRightClick;
        readonly Action<Graphics, Pen, int, int> _drawGlyph;
        readonly ToolTip _tip = new();
        bool _hover;

        public ButtonWindow(Action onClick, Action onRightClick, Action<Graphics, Pen, int, int> drawGlyph, string name, string tip)
        {
            _onClick = onClick;
            _onRightClick = onRightClick;
            _drawGlyph = drawGlyph;
            BackColor = Fill;
            DoubleBuffered = true;
            Cursor = Cursors.Hand;
            // Names it for screen readers and UI Automation.
            Text = name;
            AccessibleName = name;
            AccessibleRole = AccessibleRole.PushButton;
            _tip.SetToolTip(this, tip);
        }

        /// <summary>Three lines: a list.</summary>
        public static void DrawList(Graphics g, Pen pen, int width, int height)
        {
            float w = width * 0.42f, x = (width - w) / 2, gap = height * 0.15f, mid = height / 2f;
            for (int i = -1; i <= 1; i++) g.DrawLine(pen, x, mid + i * gap, x + w, mid + i * gap);
        }

        /// <summary>A "V", for Venice.</summary>
        public static void DrawV(Graphics g, Pen pen, int width, int height)
        {
            float w = width * 0.36f, x = (width - w) / 2, top = height * 0.32f, bottom = height * 0.68f;
            g.DrawLines(pen, new[] { new PointF(x, top), new PointF(width / 2f, bottom), new PointF(x + w, top) });
        }

        protected override void OnResize(EventArgs e)
        {
            base.OnResize(e);
            using var path = new GraphicsPath();
            path.AddEllipse(0, 0, Width, Height);
            Region = new Region(path);
            Invalidate();
        }

        protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Left) _onClick();
            else if (e.Button == MouseButtons.Right) _onRightClick();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (var fill = new SolidBrush(_hover ? FillHover : Fill)) g.FillEllipse(fill, 0, 0, Width - 1, Height - 1);
            using (var edge = new Pen(Edge, Math.Max(1, Width / 32f))) g.DrawEllipse(edge, 0.5f, 0.5f, Width - 2, Height - 2);

            using var pen = new Pen(Glyph, Math.Max(1.5f, Height / 16f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            _drawGlyph(g, pen, Width, Height);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _tip.Dispose();
            base.Dispose(disposing);
        }
    }
}
