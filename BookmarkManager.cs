namespace ClaudePowertoys;

/// <summary>
/// The bookmark manager, in Claude Desktop's style: a sidebar with a search field, All bookmarks,
/// Unsorted and your folders (a + on the Folders header adds one), a hairline you can drag, and the
/// bookmarks of the selected folder as two-line rows with a sort chip. Double-click or Enter opens,
/// F2 renames, Delete removes, dragging onto a folder moves; right-click for the rest. Searching
/// looks through every folder. Refreshes itself when bookmarks change elsewhere (Ctrl+D).
/// </summary>
sealed class BookmarkManager : Form
{
    const string AllKey = "\0all", UnsortedKey = "\0unsorted", FoldersHeader = "\0folders", FolderPrefix = "f:";

    enum Sort { Newest, Oldest, Name, Session }

    readonly float _scale;
    readonly Panel _side = new() { Dock = DockStyle.Left, BackColor = Theme.Sidebar };
    readonly SearchField _search;
    readonly NavList _nav = new() { Dock = DockStyle.Fill };
    readonly PaneSplitter _splitter = new() { Dock = DockStyle.Left };
    readonly Header _header = new() { Dock = DockStyle.Top };
    readonly BookmarkList _list = new() { Dock = DockStyle.Fill };
    Sort _sort = Sort.Newest;

    int S(float logical) => (int)Math.Round(logical * _scale);

    public BookmarkManager()
    {
        // Sizes are in logical pixels, scaled here: WinForms doesn't scale controls built in code.
        _scale = DeviceDpi / 96f;
        AutoScaleMode = AutoScaleMode.None;
        Text = "Bookmarks";
        Icon = AppIcon.Draw(S(32));
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Main;
        ForeColor = Theme.Text;
        ClientSize = new Size(S(1080), S(700));
        MinimumSize = new Size(S(720), S(440));
        KeyPreview = true;

        _search = new SearchField("Search bookmarks") { Dock = DockStyle.Fill, UiScale = _scale };
        _search.Box.TextChanged += (_, _) => FillList();
        var searchHost = new Panel { Dock = DockStyle.Top, Height = S(66), Padding = new Padding(S(12), S(16), S(12), S(10)), BackColor = Theme.Sidebar };
        searchHost.Controls.Add(_search);
        _nav.UiScale = _scale;
        _side.Width = S(280);
        _side.Padding = new Padding(0, 0, 0, S(12));
        _side.Controls.Add(_nav);
        _side.Controls.Add(searchHost);

        _splitter.UiScale = _scale;
        _splitter.Width = S(7);
        _splitter.Attach(_side);

        _header.UiScale = _scale;
        _header.Height = S(78);
        _header.SortChip.UiScale = _scale;
        _header.SortChip.Click += (_, _) => ShowSortMenu();

        _list.UiScale = _scale;
        _list.EmptyTitle = "No bookmarks here";
        _list.EmptyHint = "Point at any message in Claude and press Ctrl+D";

        var main = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Main };
        main.Controls.Add(_list);
        main.Controls.Add(_header);
        Controls.Add(main);
        Controls.Add(_splitter);
        Controls.Add(_side);

        _nav.SelectedChanged += _ => FillList();
        _nav.AddClicked += NewFolder;
        _nav.Renamed += RenameFolder;
        _nav.Dropped += (key, ids) => BookmarkStore.Move(ids, FolderOf(key));
        _nav.ContextRequested += ShowFolderMenu;
        _list.Open += BookmarkMenu.Open;
        _list.DeleteRequested += ids => BookmarkStore.Remove(ids);
        _list.Renamed += BookmarkStore.Rename;
        _list.ContextRequested += ShowBookmarkMenu;

        BookmarkStore.Changed += OnStoreChanged;
        Load += (_, _) => { Refill(AllKey); _search.Box.Focus(); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Theme.DarkWindow(Handle, Theme.Main);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Kept for next time: closing only hides it.
        if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        BookmarkStore.Changed -= OnStoreChanged;
        base.OnFormClosing(e);
    }

    public void ShowManager()
    {
        if (!Visible) Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
        Refill(_nav.Selected ?? AllKey);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Control && e.KeyCode == Keys.F) { _search.Box.Focus(); _search.Box.SelectAll(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape && _search.Box.Text != "") { _search.Box.Clear(); e.Handled = true; }
        else if (e.KeyCode == Keys.F2 && _nav.Focused && _nav.Selected is { } key && _nav.CanRename(key)) { _nav.BeginRename(key); e.Handled = true; }
        base.OnKeyDown(e);
    }

    void OnStoreChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(() => { if (Visible) Refill(_nav.Selected ?? AllKey); });
    }

    static string FolderOf(string key) => key.StartsWith(FolderPrefix) ? key[FolderPrefix.Length..] : "";

    void Refill(string select)
    {
        var all = BookmarkStore.Load();
        string Count(int n) => n == 0 ? "" : n.ToString();
        var items = new List<NavList.Item>
        {
            new(AllKey, "All bookmarks", Theme.GlyphStar, Count(all.Count)),
            new(UnsortedKey, "Unsorted", Theme.GlyphDocument, Count(all.Count(b => b.Folder == "")), CanDrop: true),
            new(FoldersHeader, "Folders", Header: true),
        };
        foreach (var f in BookmarkStore.Folders())
            items.Add(new(FolderPrefix + f, f, Theme.GlyphFolder, Count(all.Count(b => b.Folder == f)), CanRename: true, CanDrop: true));
        _nav.SetItems(items, select);
        FillList();
    }

    void FillList()
    {
        var all = BookmarkStore.Load();
        var query = _search.Box.Text.Trim();
        var key = _nav.Selected ?? AllKey;
        IEnumerable<Bookmark> shown;
        if (query != "")
        {
            shown = all.Where(b => new[] { b.Name, b.Text, b.SessionTitle, b.Folder }.Any(s => s.Contains(query, StringComparison.OrdinalIgnoreCase)));
            _header.SetTitle("Search results");
        }
        else
        {
            shown = key == AllKey ? all : all.Where(b => b.Folder == FolderOf(key));
            _header.SetTitle(key == AllKey ? "All bookmarks" : key == UnsortedKey ? "Unsorted" : FolderOf(key));
        }
        var rows = (_sort switch
        {
            Sort.Oldest => shown.OrderBy(b => b.AddedAt),
            Sort.Name => shown.OrderBy(b => b.Name, StringComparer.CurrentCultureIgnoreCase),
            Sort.Session => shown.OrderBy(b => b.SessionTitle, StringComparer.CurrentCultureIgnoreCase).ThenByDescending(b => b.AddedAt),
            _ => shown.OrderByDescending(b => b.AddedAt),
        }).ToList();
        _header.SetSubtitle(query != "" ? $"{rows.Count} found in all folders" : $"{rows.Count} bookmark{(rows.Count == 1 ? "" : "s")}");
        _header.SortChip.Text = _sort.ToString();
        _header.LayoutChip();
        _list.SetRows(rows);
    }

    void ShowSortMenu()
    {
        var menu = NewMenu();
        foreach (var sort in Enum.GetValues<Sort>())
            menu.Items.Add(new ToolStripMenuItem(sort.ToString(), null, (_, _) => { _sort = sort; FillList(); }) { Checked = sort == _sort });
        menu.Show(_header.SortChip, new Point(0, _header.SortChip.Height + S(4)));
    }

    void NewFolder()
    {
        var name = BookmarkStore.AddFolder("New folder");
        Refill(FolderPrefix + name);
        _nav.BeginRename(FolderPrefix + name);
    }

    void RenameFolder(string key, string name)
    {
        if (!key.StartsWith(FolderPrefix)) return;
        if (BookmarkStore.RenameFolder(FolderOf(key), name)) Refill(FolderPrefix + name);
        else _header.Note($"There is already a folder called \"{name}\"");
    }

    void ShowFolderMenu(string key, Point at)
    {
        if (!key.StartsWith(FolderPrefix)) return;
        var menu = NewMenu();
        menu.Items.Add("Rename", null, (_, _) => _nav.BeginRename(key));
        menu.Items.Add("Delete folder", null, (_, _) => BookmarkStore.DeleteFolder(FolderOf(key)));
        menu.Items.Add(new ToolStripMenuItem("Its bookmarks move to Unsorted") { Enabled = false });
        menu.Show(at);
    }

    void ShowBookmarkMenu(Point at)
    {
        var selected = _list.SelectedBookmarks;
        if (selected.Count == 0) return;
        var ids = selected.Select(b => b.Id).ToList();
        var menu = NewMenu();
        if (selected.Count == 1)
        {
            menu.Items.Add("Open", null, (_, _) => BookmarkMenu.Open(selected[0]));
            menu.Items.Add("Rename", null, (_, _) => _list.BeginRename());
        }
        var move = new ToolStripMenuItem("Move to");
        move.DropDown.BackColor = Theme.Card;
        move.DropDownItems.Add("Unsorted", null, (_, _) => BookmarkStore.Move(ids, ""));
        foreach (var f in BookmarkStore.Folders())
            move.DropDownItems.Add(f.Replace("&", "&&"), null, (_, _) => BookmarkStore.Move(ids, f));
        move.DropDownItems.Add(new ToolStripSeparator());
        move.DropDownItems.Add("New folder…", null, (_, _) =>
        {
            var name = BookmarkStore.AddFolder("New folder");
            BookmarkStore.Move(ids, name);
            Refill(FolderPrefix + name);
            _nav.BeginRename(FolderPrefix + name);
        });
        menu.Items.Add(move);
        menu.Items.Add("Copy message text", null, (_, _) => Clipboard.SetText(string.Join("\n\n", selected.Select(b => b.Text))));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(selected.Count == 1 ? "Delete" : $"Delete {selected.Count} bookmarks", null, (_, _) => BookmarkStore.Remove(ids));
        menu.Show(at);
    }

    ContextMenuStrip NewMenu()
    {
        var menu = new ContextMenuStrip { Renderer = DarkMenu.Renderer, ShowImageMargin = false, Font = Theme.Ui(S(14)) };
        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose);
        return menu;
    }

    /// <summary>The list's header: the folder's name, a count, a passing note, and the sort chip.</summary>
    sealed class Header : ThemedControl
    {
        public readonly Chip SortChip = new();
        string _title = "", _subtitle = "", _note = "";
        readonly System.Windows.Forms.Timer _noteTimer = new() { Interval = 3500 };

        public Header()
        {
            BackColor = Theme.Main;
            Controls.Add(SortChip);
            _noteTimer.Tick += (_, _) => { _noteTimer.Stop(); _note = ""; Invalidate(); };
        }

        public void SetTitle(string value) { _title = value; Invalidate(); }
        public void SetSubtitle(string value) { _subtitle = value; Invalidate(); }

        public void Note(string text)
        {
            _note = text;
            _noteTimer.Stop();
            _noteTimer.Start();
            Invalidate();
        }

        public void LayoutChip()
        {
            int w = SortChip.PreferredWidth(), h = S(30);
            SortChip.SetBounds(Width - w - S(24), S(26), w, h);
        }

        protected override void OnResize(EventArgs e) { base.OnResize(e); LayoutChip(); }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            Prepare(g);
            g.Clear(Theme.Main);
            using var title = Theme.Ui(S(21), semibold: true);
            using var small = Theme.Ui(S(13.5f));
            int left = S(32), width = SortChip.Left - left - S(16);
            Draw(g, _title, title, new Rectangle(left, S(14), width, S(34)), Theme.Text);
            Draw(g, _note != "" ? _note : _subtitle, small, new Rectangle(left, S(46), width, S(22)), _note != "" ? Theme.Accent : Theme.Dim);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _noteTimer.Dispose();
            base.Dispose(disposing);
        }
    }
}
