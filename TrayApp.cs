using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ClaudePowertoys;

/// <summary>Notification-area icon and its right-click menu.</summary>
sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon _icon;
    readonly Settings _settings = Settings.Load();
    readonly ResizeHandleBlocker _handleBlocker = new();
    readonly SessionSwitcher _switcher = new();
    readonly SendNowShortcut _sendNow = new();
    readonly OutlineButton _outline = new();
    readonly PromptJumpShortcut _promptJump = new();
    readonly SnippetInserter _snippets = new();
    readonly SidebarAutoHide _autoHide;
    readonly GlobalHotkey _needsInputHotkey;
    readonly StatusMonitor _status;
    readonly SynchronizationContext _ui;
    List<(SessionInfo Session, SessionStatusInfo Status)> _waiting = [];

    public TrayApp()
    {
        // The forms created above installed WinForms' synchronization context on this thread.
        _ui = SynchronizationContext.Current!;
        _status = new StatusMonitor(waiting => _ui.Post(_ => ShowWaiting(waiting), null));
        _switcher.StatusOf = _status.Get;
        _autoHide = new SidebarAutoHide(_handleBlocker.OverlayHandle);
        _needsInputHotkey = new GlobalHotkey(_settings.NeedsInputHotkeyKeys, () =>
        {
            Log.Write($"{_settings.NeedsInputHotkeyKeys}: opening sessions waiting for you");
            ClaudeLinks.Open(ClaudeLinks.NeedsInput);
        });

        var menu = new ContextMenuStrip();
        menu.Items.Add(Toggle("Auto-hide sidebar", _settings.AutoHideSidebar, on =>
        {
            _settings.AutoHideSidebar = on;
            _autoHide.Enabled = on;
        }));
        menu.Items.Add(Toggle("Remove sidebar resize handle", _settings.RemoveResizeHandle, on =>
        {
            _settings.RemoveResizeHandle = on;
            _handleBlocker.Enabled = on;
        }));
        menu.Items.Add(Toggle("Ctrl+Tab session switcher", _settings.SessionSwitcher, on =>
        {
            _settings.SessionSwitcher = on;
            _switcher.Enabled = on;
        }));
        menu.Items.Add(Toggle("Ctrl+Enter sends now", _settings.CtrlEnterSendsNow, on =>
        {
            _settings.CtrlEnterSendsNow = on;
            _sendNow.Enabled = on;
        }));
        menu.Items.Add(Toggle("Conversation outline button", _settings.OutlineButton, on =>
        {
            _settings.OutlineButton = on;
            _outline.Enabled = on;
        }));
        menu.Items.Add(Toggle("Ctrl+Up/Down jumps between prompts", _settings.PromptJump, on =>
        {
            _settings.PromptJump = on;
            _promptJump.Enabled = on;
        }));
        menu.Items.Add(Toggle("Ctrl+Shift+Space: insert a snippet", _settings.Snippets, on =>
        {
            _settings.Snippets = on;
            _snippets.Enabled = on;
        }));
        var snippetsMenu = new ToolStripMenuItem("Snippets");
        // Re-read on every opening, so edits to snippets.json show up without a restart.
        snippetsMenu.DropDownOpening += (_, _) =>
        {
            snippetsMenu.DropDownItems.Clear();
            SnippetInserter.AddItems(snippetsMenu.DropDownItems);
            snippetsMenu.DropDownItems.Add(new ToolStripSeparator());
            snippetsMenu.DropDownItems.Add("Edit snippets…", null, (_, _) => SnippetInserter.OpenFile());
        };
        snippetsMenu.DropDownItems.Add("(loading)");
        menu.Items.Add(snippetsMenu);
        var hotkeyText = $"{_settings.NeedsInputHotkeyKeys}: sessions waiting for you";
        ToolStripMenuItem? hotkeyItem = null;
        hotkeyItem = Toggle(hotkeyText, _settings.NeedsInputHotkey, on =>
        {
            _settings.NeedsInputHotkey = on;
            _needsInputHotkey.Enabled = on;
            hotkeyItem!.Text = on && !_needsInputHotkey.Enabled ? hotkeyText + " (taken by another program)" : hotkeyText;
        });
        menu.Items.Add(hotkeyItem);
        menu.Items.Add(new ToolStripSeparator());
        var startItem = new ToolStripMenuItem("Start with Windows") { CheckOnClick = true, Checked = StartWithWindows.IsOn };
        startItem.CheckedChanged += (_, _) =>
        {
            StartWithWindows.Set(startItem.Checked);
            Log.Write($"Start with Windows {OnOff(StartWithWindows.IsOn)}");
        };
        menu.Items.Add(startItem);
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = DrawIcon(0),
            Text = "Claude Powertoys",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button != MouseButtons.Left) return;
            // The first waiting session, or the app's own list when there is none to open.
            var link = _waiting.Count > 0 ? _waiting[0].Session.Link : ClaudeLinks.NeedsInput;
            Log.Write($"Tray click: opening {(_waiting.Count > 0 ? $"'{_waiting[0].Session.Title}'" : "sessions waiting for you")}");
            ClaudeLinks.Open(link);
        };

        _autoHide.Enabled = _settings.AutoHideSidebar;
        _autoHide.Start();
        _handleBlocker.Enabled = _settings.RemoveResizeHandle;
        _handleBlocker.Start();
        _switcher.Enabled = _settings.SessionSwitcher;
        _switcher.RecentWindow = TimeSpan.FromHours(_settings.RecentSessionHours);
        _sendNow.Enabled = _settings.CtrlEnterSendsNow;
        _outline.Enabled = _settings.OutlineButton;
        _outline.Start();
        _promptJump.Enabled = _settings.PromptJump;
        _snippets.Enabled = _settings.Snippets;
        _status.RecentWindow = TimeSpan.FromHours(_settings.RecentSessionHours);
        _status.Start();
        _needsInputHotkey.Enabled = _settings.NeedsInputHotkey;
        if (_settings.NeedsInputHotkey && !_needsInputHotkey.Enabled) hotkeyItem.Text = hotkeyText + " (taken by another program)";
        Log.Write("Started: " + string.Join(", ", menu.Items.OfType<ToolStripMenuItem>()
            .Where(i => i.CheckOnClick).Select(i => $"{i.Text} {OnOff(i.Checked)}")));
    }

    ToolStripMenuItem Toggle(string text, bool initial, Action<bool> apply)
    {
        var item = new ToolStripMenuItem(text) { CheckOnClick = true, Checked = initial };
        item.CheckedChanged += (_, _) =>
        {
            apply(item.Checked);
            _settings.Save();
            Log.Write($"{text} {OnOff(item.Checked)}");
        };
        return item;
    }

    static string OnOff(bool on) => on ? "on" : "off";

    protected override void ExitThreadCore()
    {
        _autoHide.Dispose();
        _handleBlocker.Dispose();
        _switcher.Dispose();
        _sendNow.Dispose();
        _outline.Dispose();
        _promptJump.Dispose();
        _snippets.Dispose();
        _needsInputHotkey.Dispose();
        _status.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        Log.Write("Exited");
        base.ExitThreadCore();
    }

    void ShowWaiting(List<(SessionInfo Session, SessionStatusInfo Status)> waiting)
    {
        _waiting = waiting;
        var old = _icon.Icon;
        _icon.Icon = DrawIcon(waiting.Count);
        if (old != null) DestroyIcon(old.Handle);

        var text = waiting.Count == 0 ? "Claude Powertoys"
            : $"{waiting.Count} waiting: " + string.Join(", ", waiting.Select(w => w.Session.Title));
        // The notification-area tooltip holds at most 127 characters.
        _icon.Text = text.Length <= 127 ? text : text[..126] + "…";
    }

    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);

    /// <param name="waiting">Drawn as a badge in the corner when above zero.</param>
    static Icon DrawIcon(int waiting)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            using var path = new GraphicsPath();
            path.AddArc(0, 0, 12, 12, 180, 90);
            path.AddArc(19, 0, 12, 12, 270, 90);
            path.AddArc(19, 19, 12, 12, 0, 90);
            path.AddArc(0, 19, 12, 12, 90, 90);
            path.CloseFigure();
            using var fill = new SolidBrush(Color.FromArgb(217, 119, 87));
            g.FillPath(fill, path);
            using var font = new Font("Segoe UI", 15, FontStyle.Bold, GraphicsUnit.Pixel);
            using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString("PT", font, Brushes.White, new RectangleF(0, 1, 32, 32), format);
            if (waiting > 0)
            {
                using var badge = new SolidBrush(Color.FromArgb(200, 40, 40));
                g.FillEllipse(badge, 13, 13, 19, 19);
                g.DrawEllipse(Pens.White, 13, 13, 18, 18);
                using var small = new Font("Segoe UI", waiting > 9 ? 10 : 13, FontStyle.Bold, GraphicsUnit.Pixel);
                g.DrawString(waiting > 9 ? "9+" : waiting.ToString(), small, Brushes.White, new RectangleF(13, 14, 19, 19), format);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
