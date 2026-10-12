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
    readonly PromptHistoryRecall _history = new();
    readonly BookmarkShortcut _bookmarks = new();
    readonly BookmarkManager _bookmarkManager = new();
    readonly SidebarAutoHide _autoHide;
    readonly GlobalHotkey _needsInputHotkey;
    readonly GlobalHotkey _newSessionHotkey;
    readonly GlobalHotkey _searchHotkey;
    readonly SearchWindow _search = new();
    readonly ForegroundWatcher _foreground;
    readonly Control _menuOwner = new();
    readonly StatusMonitor _status;
    readonly SynchronizationContext _ui;
    List<(SessionInfo Session, SessionStatusInfo Status)> _waiting = [];

    public TrayApp()
    {
        // The forms created above installed WinForms' synchronization context on this thread.
        _ui = SynchronizationContext.Current!;
        _status = new StatusMonitor(snapshot => _ui.Post(_ => ShowStatus(snapshot), null));
        _switcher.StatusOf = _status.Get;
        _autoHide = new SidebarAutoHide(_handleBlocker.OverlayHandle);
        _needsInputHotkey = new GlobalHotkey(_settings.NeedsInputHotkeyKeys, () =>
        {
            Log.Write($"{_settings.NeedsInputHotkeyKeys}: opening sessions waiting for you");
            ClaudeLinks.Open(ClaudeLinks.NeedsInput);
        });
        _ = _menuOwner.Handle;
        _newSessionHotkey = new GlobalHotkey(_settings.NewSessionHotkeyKeys, () => NewSessionMenu.Show(_menuOwner));

        // A registered hotkey (unlike a keyboard hook) lets the search window take the keyboard. It is
        // registered only while Claude is the active window, so other apps keep the combination.
        _searchHotkey = new GlobalHotkey(_settings.SearchHotkeyKeys, () => _search.Open(ClaudeWindow.Find()));
        _foreground = new ForegroundWatcher(hwnd => _searchHotkey.Enabled = _settings.Search && hwnd != IntPtr.Zero && hwnd == ClaudeWindow.Find());
        // The watcher only reports changes; Claude may already be in front.
        _searchHotkey.Enabled = _settings.Search && ClaudeWindow.IsInFrontSlow();
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { SearchIndex.Warm(); }
            catch (Exception ex) { Log.Write($"Search index: {ex.Message}"); }
        });

        var menu = new ContextMenuStrip { Renderer = DarkMenu.Renderer, Font = Theme.Ui(14 * (_menuOwner.DeviceDpi / 96f)) };
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
        menu.Items.Add(Toggle($"{_settings.SearchHotkeyKeys}: search all sessions", _settings.Search, on =>
        {
            _settings.Search = on;
            if (!on) _searchHotkey.Enabled = false;
        }));
        menu.Items.Add(Toggle("Alt+Up: earlier prompts", _settings.PromptHistory, on =>
        {
            _settings.PromptHistory = on;
            _history.Enabled = on;
        }));
        menu.Items.Add(Toggle("Ctrl+D: bookmark the message under the mouse", _settings.Bookmarks, on =>
        {
            _settings.Bookmarks = on;
            _bookmarks.Enabled = on;
        }));
        BookmarkMenu.OpenManager = _bookmarkManager.ShowManager;
        var bookmarksMenu = new ToolStripMenuItem("Bookmarks");
        // Re-read on every opening: Ctrl+D changes the list.
        bookmarksMenu.DropDownOpening += (_, _) =>
        {
            bookmarksMenu.DropDownItems.Clear();
            BookmarkMenu.AddItems(bookmarksMenu.DropDownItems);
        };
        bookmarksMenu.DropDownItems.Add("(loading)");
        menu.Items.Add(bookmarksMenu);
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
        menu.Items.Add(HotkeyToggle("sessions waiting for you", _needsInputHotkey, _settings.NeedsInputHotkey, on => _settings.NeedsInputHotkey = on));
        menu.Items.Add(HotkeyToggle("new session in a recent folder", _newSessionHotkey, _settings.NewSessionHotkey, on => _settings.NewSessionHotkey = on));
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
            Icon = AppIcon.Draw(AppIcon.TraySize),
            Text = "Claude Powertoys",
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            // Only ever goes to a session needing attention; with none, a click does nothing.
            if (e.Button != MouseButtons.Left || _waiting.Count == 0) return;
            Log.Write($"Tray click: opening '{_waiting[0].Session.Title}'");
            ClaudeLinks.Open(_waiting[0].Session.Link);
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
        _history.Enabled = _settings.PromptHistory;
        _bookmarks.Enabled = _settings.Bookmarks;
        _status.RecentWindow = TimeSpan.FromHours(_settings.RecentSessionHours);
        _status.Start();
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

    /// <summary>A switch for a global hotkey, registered right away; says so when another program owns the keys.</summary>
    ToolStripMenuItem HotkeyToggle(string action, GlobalHotkey hotkey, bool initial, Action<bool> save)
    {
        var text = $"{hotkey.Text}: {action}";
        ToolStripMenuItem? item = null;
        void Apply(bool on)
        {
            hotkey.Enabled = on;
            item!.Text = on && !hotkey.Enabled ? text + " (taken by another program)" : text;
        }
        item = Toggle(text, initial, on =>
        {
            save(on);
            Apply(on);
        });
        Apply(initial);
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
        _history.Dispose();
        _bookmarks.Dispose();
        _bookmarkManager.Dispose();
        _needsInputHotkey.Dispose();
        _newSessionHotkey.Dispose();
        _foreground.Dispose();
        _searchHotkey.Dispose();
        _search.Dispose();
        _menuOwner.Dispose();
        _status.Dispose();
        _icon.Visible = false;
        _icon.Dispose();
        Log.Write("Exited");
        base.ExitThreadCore();
    }

    void ShowStatus(StatusSnapshot snapshot)
    {
        var waiting = snapshot.Waiting;
        if (waiting.Count != _waiting.Count)
        {
            var old = _icon.Icon;
            _icon.Icon = AppIcon.Draw(AppIcon.TraySize, waiting.Count);
            old?.Dispose();
        }
        _waiting = waiting;

        var lines = new List<string>();
        if (waiting.Count > 0) lines.Add($"{waiting.Count} waiting: " + string.Join(", ", waiting.Select(w => w.Session.Title)));
        if (snapshot.Working.Count > 0) lines.Add("Working: " + string.Join(", ", snapshot.Working.Select(w => $"{w.Session.Title} ({w.Status.TurnAge()})")));
        var text = lines.Count == 0 ? "Claude Powertoys" : string.Join("\n", lines);
        // The notification-area tooltip holds at most 127 characters.
        text = text.Length <= 127 ? text : text[..126] + "…";
        if (_icon.Text != text) _icon.Text = text;
    }

}
