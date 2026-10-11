namespace ClaudePowertoys;

/// <summary>
/// A browser-style bookmark manager: folders on the left (All bookmarks, Unsorted, then your folders),
/// the bookmarks of the selected folder on the right, and a search box across everything. Double-click
/// or Enter opens, F2 renames, Delete removes, dragging onto a folder moves; right-click for the rest.
/// Refreshes itself when bookmarks change elsewhere (Ctrl+D).
/// </summary>
sealed class BookmarkManager : Form
{
    static readonly Color Background = Color.FromArgb(38, 38, 36);
    static readonly Color Panel = Color.FromArgb(46, 46, 43);
    static readonly Color Field = Color.FromArgb(56, 56, 52);
    static readonly Color Line = Color.FromArgb(70, 70, 66);
    static readonly Color RowText = Color.FromArgb(240, 238, 230);
    static readonly Color DimText = Color.FromArgb(160, 158, 150);

    const string AllNode = "\0all", UnsortedNode = "";

    readonly TextBox _search = new();
    readonly TreeView _folders = new();
    readonly ListView _list = new();
    readonly Label _status = new();
    int _sortColumn = 3;
    bool _sortAscending;

    public BookmarkManager()
    {
        Text = "Bookmarks: Claude Powertoys";
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 10f);
        BackColor = Background;
        ForeColor = RowText;
        ClientSize = new Size(1100, 640);
        MinimumSize = new Size(700, 400);
        KeyPreview = true;

        _search.Dock = DockStyle.Fill;
        _search.BorderStyle = BorderStyle.FixedSingle;
        _search.BackColor = Field;
        _search.ForeColor = RowText;
        _search.PlaceholderText = "Search bookmarks";
        _search.TextChanged += (_, _) => Fill();

        var top = new Panel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(10, 10, 10, 6), BackColor = Background };
        top.Controls.Add(_search);

        _folders.Dock = DockStyle.Fill;
        _folders.BorderStyle = BorderStyle.None;
        _folders.BackColor = Panel;
        _folders.ForeColor = RowText;
        _folders.LineColor = Line;
        _folders.HideSelection = false;
        _folders.FullRowSelect = true;
        _folders.ShowLines = false;
        _folders.ShowRootLines = false;
        _folders.ItemHeight = 30;
        _folders.LabelEdit = true;
        _folders.AllowDrop = true;
        _folders.AfterSelect += (_, _) => Fill();
        _folders.BeforeLabelEdit += (_, e) => e.CancelEdit = !IsUserFolder(e.Node);
        _folders.AfterLabelEdit += RenameFolder;
        _folders.DragOver += FolderDragOver;
        _folders.DragDrop += FolderDragDrop;
        _folders.NodeMouseClick += (_, e) => { if (e.Button == MouseButtons.Right) _folders.SelectedNode = e.Node; };
        _folders.ContextMenuStrip = FolderMenu();

        _list.Dock = DockStyle.Fill;
        _list.View = View.Details;
        _list.FullRowSelect = true;
        _list.HideSelection = false;
        _list.BorderStyle = BorderStyle.None;
        _list.BackColor = Background;
        _list.ForeColor = RowText;
        _list.LabelEdit = true;
        _list.OwnerDraw = true;
        _list.Columns.Add("Name", 460);
        _list.Columns.Add("Session", 280);
        _list.Columns.Add("By", 70);
        _list.Columns.Add("Added", 130);
        _list.DrawColumnHeader += DrawHeader;
        _list.DrawItem += (_, e) => e.DrawDefault = true;
        _list.DrawSubItem += (_, e) => e.DrawDefault = true;
        _list.ColumnClick += (_, e) => { _sortAscending = e.Column == _sortColumn ? !_sortAscending : e.Column != 3; _sortColumn = e.Column; Fill(); };
        _list.ItemActivate += (_, _) => OpenSelected();
        _list.AfterLabelEdit += RenameBookmark;
        _list.ItemDrag += (_, _) => DoDragDrop(SelectedIds().ToArray(), DragDropEffects.Move);
        _list.ContextMenuStrip = BookmarkMenuStrip();

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 240, BackColor = Line, SplitterWidth = 1 };
        split.Panel1.Controls.Add(_folders);
        split.Panel1.BackColor = Panel;
        split.Panel2.Controls.Add(_list);

        _status.Dock = DockStyle.Bottom;
        _status.Height = 28;
        _status.Padding = new Padding(10, 4, 10, 4);
        _status.ForeColor = DimText;
        _status.BackColor = Background;

        Controls.Add(split);
        Controls.Add(top);
        Controls.Add(_status);

        BookmarkStore.Changed += OnStoreChanged;
        Load += (_, _) => { FillFolders(AllNode); _search.Focus(); };
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
        FillFolders(SelectedFolder());
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && _search.Text != "") { _search.Clear(); e.Handled = true; }
        else if (e.KeyCode == Keys.Delete && _list.Focused) { DeleteSelected(); e.Handled = true; }
        else if (e.KeyCode == Keys.F2 && _list.Focused && _list.SelectedItems.Count == 1) { _list.SelectedItems[0].BeginEdit(); e.Handled = true; }
        else if (e.KeyCode == Keys.F2 && _folders.Focused && IsUserFolder(_folders.SelectedNode)) { _folders.SelectedNode!.BeginEdit(); e.Handled = true; }
        else if (e.KeyCode == Keys.Enter && _list.Focused) { OpenSelected(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.F) { _search.Focus(); _search.SelectAll(); e.Handled = true; }
        base.OnKeyDown(e);
    }

    void OnStoreChanged()
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(() => { if (Visible) FillFolders(SelectedFolder()); });
    }

    // ---- folders ----

    string SelectedFolder() => _folders.SelectedNode?.Tag as string ?? AllNode;

    static bool IsUserFolder(TreeNode? node) => node?.Tag is string tag && tag != AllNode && tag != UnsortedNode;

    void FillFolders(string select)
    {
        var bookmarks = BookmarkStore.Load();
        _folders.BeginUpdate();
        _folders.Nodes.Clear();
        _folders.Nodes.Add(new TreeNode($"★  All bookmarks ({bookmarks.Count})") { Tag = AllNode });
        _folders.Nodes.Add(new TreeNode($"📥  Unsorted ({bookmarks.Count(b => b.Folder == "")})") { Tag = UnsortedNode });
        foreach (var f in BookmarkStore.Folders())
            _folders.Nodes.Add(new TreeNode($"📁  {f} ({bookmarks.Count(b => b.Folder == f)})") { Tag = f, Name = f });
        _folders.SelectedNode = _folders.Nodes.Cast<TreeNode>().FirstOrDefault(n => n.Tag as string == select) ?? _folders.Nodes[0];
        _folders.EndUpdate();
        Fill();
    }

    ContextMenuStrip FolderMenu()
    {
        var menu = new ContextMenuStrip { Renderer = DarkMenu.Renderer, ShowImageMargin = false };
        var rename = new ToolStripMenuItem("Rename\tF2", null, (_, _) => _folders.SelectedNode?.BeginEdit());
        var delete = new ToolStripMenuItem("Delete folder (bookmarks move to Unsorted)", null, (_, _) =>
        {
            if (IsUserFolder(_folders.SelectedNode) && _folders.SelectedNode!.Tag is string folder) BookmarkStore.DeleteFolder(folder);
        });
        menu.Items.Add("New folder", null, (_, _) => NewFolder());
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.Opening += (_, _) => rename.Enabled = delete.Enabled = IsUserFolder(_folders.SelectedNode);
        return menu;
    }

    void NewFolder()
    {
        var name = BookmarkStore.AddFolder("New folder");
        FillFolders(name);
        _folders.SelectedNode?.BeginEdit();
    }

    void RenameFolder(object? sender, NodeLabelEditEventArgs e)
    {
        e.CancelEdit = true; // The list is rebuilt from the store either way.
        if (e.Label == null || e.Node?.Tag is not string from) return;
        if (!BookmarkStore.RenameFolder(from, e.Label)) _status.Text = $"There is already a folder called \"{e.Label.Trim()}\"";
        else FillFolders(e.Label.Trim());
    }

    void FolderDragOver(object? sender, DragEventArgs e)
    {
        var node = _folders.GetNodeAt(_folders.PointToClient(new Point(e.X, e.Y)));
        bool canDrop = e.Data?.GetDataPresent(typeof(string[])) == true && node?.Tag is string tag && tag != AllNode;
        e.Effect = canDrop ? DragDropEffects.Move : DragDropEffects.None;
        if (canDrop) _folders.SelectedNode = node;
    }

    void FolderDragDrop(object? sender, DragEventArgs e)
    {
        var node = _folders.GetNodeAt(_folders.PointToClient(new Point(e.X, e.Y)));
        if (e.Data?.GetData(typeof(string[])) is string[] ids && node?.Tag is string folder && folder != AllNode)
            BookmarkStore.Move(ids, folder);
    }

    // ---- bookmarks ----

    void Fill()
    {
        var query = _search.Text.Trim();
        var folder = SelectedFolder();
        var all = BookmarkStore.Load();
        // Searching looks through every folder, as in a browser.
        var shown = query != ""
            ? all.Where(b => new[] { b.Name, b.Text, b.SessionTitle, b.Folder }.Any(s => s.Contains(query, StringComparison.OrdinalIgnoreCase)))
            : folder == AllNode ? all : all.Where(b => b.Folder == folder);

        Func<Bookmark, IComparable> key = _sortColumn switch
        {
            0 => b => b.Name,
            1 => b => b.SessionTitle,
            2 => b => b.FromUser ? "You" : "Claude",
            _ => b => b.AddedAt,
        };
        var sorted = (_sortAscending ? shown.OrderBy(key) : shown.OrderByDescending(key)).ToList();

        _list.BeginUpdate();
        _list.Items.Clear();
        foreach (var b in sorted)
        {
            var added = DateTimeOffset.FromUnixTimeMilliseconds(b.AddedAt).LocalDateTime.ToString("MMM d, HH:mm");
            _list.Items.Add(new ListViewItem([b.Name, b.SessionTitle, b.FromUser ? "You" : "Claude", added]) { Tag = b, ToolTipText = b.Text });
        }
        _list.ShowItemToolTips = true;
        _list.EndUpdate();
        _status.Text = query != "" ? $"{sorted.Count} found in all folders" : $"{sorted.Count} bookmark{(sorted.Count == 1 ? "" : "s")}";
    }

    IEnumerable<string> SelectedIds() => _list.SelectedItems.Cast<ListViewItem>().Select(i => ((Bookmark)i.Tag!).Id);

    void OpenSelected()
    {
        if (_list.SelectedItems.Count == 0) return;
        BookmarkMenu.Open((Bookmark)_list.SelectedItems[0].Tag!);
    }

    void DeleteSelected()
    {
        var ids = SelectedIds().ToList();
        if (ids.Count > 0) BookmarkStore.Remove(ids);
    }

    void RenameBookmark(object? sender, LabelEditEventArgs e)
    {
        e.CancelEdit = true; // The list is rebuilt from the store either way.
        if (e.Label != null) BookmarkStore.Rename(((Bookmark)_list.Items[e.Item].Tag!).Id, e.Label);
    }

    ContextMenuStrip BookmarkMenuStrip()
    {
        var menu = new ContextMenuStrip { Renderer = DarkMenu.Renderer, ShowImageMargin = false };
        var open = new ToolStripMenuItem("Open\tEnter", null, (_, _) => OpenSelected());
        var rename = new ToolStripMenuItem("Rename\tF2", null, (_, _) => { if (_list.SelectedItems.Count == 1) _list.SelectedItems[0].BeginEdit(); });
        var move = new ToolStripMenuItem("Move to");
        var copy = new ToolStripMenuItem("Copy message text", null, (_, _) =>
            Clipboard.SetText(string.Join("\n\n", _list.SelectedItems.Cast<ListViewItem>().Select(i => ((Bookmark)i.Tag!).Text))));
        var delete = new ToolStripMenuItem("Delete\tDel", null, (_, _) => DeleteSelected());
        menu.Items.AddRange([open, rename, move, copy, new ToolStripSeparator(), delete]);
        menu.Opening += (_, e) =>
        {
            int n = _list.SelectedItems.Count;
            if (n == 0) { e.Cancel = true; return; }
            open.Enabled = rename.Enabled = n == 1;
            move.DropDownItems.Clear();
            move.DropDownItems.Add("Unsorted", null, (_, _) => BookmarkStore.Move(SelectedIds().ToList(), ""));
            foreach (var f in BookmarkStore.Folders())
                move.DropDownItems.Add(f.Replace("&", "&&"), null, (_, _) => BookmarkStore.Move(SelectedIds().ToList(), f));
            move.DropDownItems.Add(new ToolStripSeparator());
            move.DropDownItems.Add("New folder…", null, (_, _) =>
            {
                var ids = SelectedIds().ToList();
                var name = BookmarkStore.AddFolder("New folder");
                BookmarkStore.Move(ids, name);
                FillFolders(name);
                _folders.SelectedNode?.BeginEdit();
            });
        };
        return menu;
    }

    void DrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using (var back = new SolidBrush(Panel)) e.Graphics.FillRectangle(back, e.Bounds);
        using (var line = new Pen(Line)) e.Graphics.DrawLine(line, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        var arrow = e.ColumnIndex == _sortColumn ? (_sortAscending ? "  ▲" : "  ▼") : "";
        TextRenderer.DrawText(e.Graphics, e.Header!.Text + arrow, Font, Rectangle.Inflate(e.Bounds, -6, 0), DimText,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
