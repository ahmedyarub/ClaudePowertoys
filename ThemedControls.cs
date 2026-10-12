using System.Drawing.Drawing2D;

namespace ClaudePowertoys;

/// <summary>A control drawn entirely by hand, double-buffered, sized in logical pixels scaled by <see cref="UiScale"/>.</summary>
abstract class ThemedControl : Control
{
    protected ThemedControl()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public float UiScale { get; set; } = 1;
    protected int S(float logical) => (int)Math.Round(logical * UiScale);

    protected static void Prepare(Graphics g)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;
    }

    protected static void Draw(Graphics g, string text, Font font, Rectangle r, Color color, bool center = false, bool right = false) =>
        TextRenderer.DrawText(g, text, font, r, color,
            (center ? TextFormatFlags.HorizontalCenter : right ? TextFormatFlags.Right : TextFormatFlags.Left)
            | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine
            // One character is an icon glyph: never cut it to an ellipsis.
            | (text.Length > 1 ? TextFormatFlags.EndEllipsis : TextFormatFlags.NoClipping));
}

/// <summary>Claude's search field: a rounded, outlined box with a magnifier and a borderless text box.</summary>
sealed class SearchField : ThemedControl
{
    public readonly TextBox Box = new() { BorderStyle = BorderStyle.None, BackColor = Theme.Field, ForeColor = Theme.Text };

    public SearchField(string placeholder)
    {
        Box.PlaceholderText = placeholder;
        Controls.Add(Box);
        Cursor = Cursors.IBeam;
        Click += (_, _) => Box.Focus();
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        Box.Font = Theme.Ui(S(15));
        int left = S(38);
        Box.SetBounds(left, (Height - Box.PreferredHeight) / 2, Width - left - S(12), Box.PreferredHeight);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        g.Clear(Parent?.BackColor ?? Theme.Sidebar);
        var r = new RectangleF(0.5f, 0.5f, Width - 1, Height - 1);
        Theme.FillRounded(g, Theme.Field, r, S(10));
        Theme.DrawRounded(g, Box.Focused ? Theme.Bullet : Theme.FieldBorder, Math.Max(1, S(1)), r, S(10));
        using var icons = Theme.Icons(S(15));
        Draw(g, Theme.GlyphSearch, icons, new Rectangle(S(12), 0, S(20), Height), Theme.Dim);
    }

    protected override void OnCreateControl()
    {
        base.OnCreateControl();
        Box.GotFocus += (_, _) => Invalidate();
        Box.LostFocus += (_, _) => Invalidate();
    }
}

/// <summary>
/// Claude's sidebar list: rows with a line icon, rounded highlight on hover and selection, section
/// headers in the secondary colour with an optional + button, a count on the right, drop targets,
/// and inline renaming.
/// </summary>
sealed class NavList : ThemedControl
{
    public sealed record Item(string Key, string Text, string Glyph = "", string Count = "", bool Header = false, bool CanRename = false, bool CanDrop = false);

    List<Item> _items = [];
    int _hover = -1, _drop = -1;
    bool _hoverAdd;
    TextBox? _rename;

    public string? Selected { get; private set; }
    public event Action<string>? SelectedChanged;
    public event Action? AddClicked;
    public event Action<string, Point>? ContextRequested;
    public event Action<string, string>? Renamed;
    public event Action<string, string[]>? Dropped;

    public NavList()
    {
        AllowDrop = true;
        BackColor = Theme.Sidebar;
    }

    int RowHeight => S(36);
    int HeaderHeight => S(40);

    public void SetItems(List<Item> items, string? select)
    {
        _items = items;
        Selected = items.FirstOrDefault(i => i.Key == select && !i.Header)?.Key ?? items.FirstOrDefault(i => !i.Header)?.Key;
        Invalidate();
    }

    Rectangle RowRect(int index)
    {
        int y = 0;
        for (int i = 0; i < index; i++) y += _items[i].Header ? HeaderHeight : RowHeight;
        return new Rectangle(S(10), y, Width - S(20), _items[index].Header ? HeaderHeight : RowHeight);
    }

    int IndexAt(Point p)
    {
        int y = 0;
        for (int i = 0; i < _items.Count; i++)
        {
            int h = _items[i].Header ? HeaderHeight : RowHeight;
            if (p.Y >= y && p.Y < y + h) return i;
            y += h;
        }
        return -1;
    }

    Rectangle AddRect(int headerIndex)
    {
        var r = RowRect(headerIndex);
        return new Rectangle(r.Right - S(28), r.Y + (r.Height - S(26)) / 2 + S(4), S(26), S(26));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        g.Clear(Theme.Sidebar);
        using var font = Theme.Ui(S(15));
        using var small = Theme.Ui(S(13.5f));
        using var icons = Theme.Icons(S(15));
        for (int i = 0; i < _items.Count; i++)
        {
            var item = _items[i];
            var r = RowRect(i);
            if (item.Header)
            {
                Draw(g, item.Text, small, new Rectangle(r.X + S(10), r.Y + S(6), r.Width - S(50), r.Height - S(6)), Theme.Dim);
                var add = AddRect(i);
                if (_hoverAdd && _hover == i) Theme.FillRounded(g, Theme.Hover, add, S(6));
                Draw(g, Theme.GlyphAdd, icons, add, _hoverAdd && _hover == i ? Theme.Text : Theme.Dim, center: true);
                continue;
            }
            bool selected = item.Key == Selected;
            if (selected || i == _drop) Theme.FillRounded(g, Theme.Selected, r, S(8));
            else if (i == _hover) Theme.FillRounded(g, Theme.Hover, r, S(8));
            var color = selected || i == _drop ? Theme.Text : Theme.RowText;
            if (item.Glyph != "") Draw(g, item.Glyph, icons, new Rectangle(r.X + S(10), r.Y, S(22), r.Height), color);
            int textLeft = r.X + S(item.Glyph == "" ? 12 : 40);
            int countWidth = item.Count == "" ? 0 : S(40);
            Draw(g, item.Text, font, new Rectangle(textLeft, r.Y, r.Right - textLeft - countWidth - S(8), r.Height), color);
            if (countWidth > 0) Draw(g, item.Count, small, new Rectangle(r.Right - countWidth - S(10), r.Y, countWidth, r.Height), Theme.Dim, right: true);
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int hover = IndexAt(e.Location);
        bool hoverAdd = hover >= 0 && _items[hover].Header && AddRect(hover).Contains(e.Location);
        Cursor = hoverAdd || (hover >= 0 && !_items[hover].Header) ? Cursors.Hand : Cursors.Default;
        if (hover == _hover && hoverAdd == _hoverAdd) return;
        _hover = hover;
        _hoverAdd = hoverAdd;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        int i = IndexAt(e.Location);
        if (i < 0) return;
        if (_items[i].Header)
        {
            if (e.Button == MouseButtons.Left && AddRect(i).Contains(e.Location)) AddClicked?.Invoke();
            return;
        }
        Select(_items[i].Key);
        if (e.Button == MouseButtons.Right) ContextRequested?.Invoke(_items[i].Key, PointToScreen(e.Location));
    }

    public void Select(string key)
    {
        if (Selected == key) return;
        Selected = key;
        Invalidate();
        SelectedChanged?.Invoke(key);
    }

    public bool CanRename(string key) => _items.Any(i => i.Key == key && i.CanRename);

    /// <summary>Edits the row's text in place; Enter or leaving commits, Esc cancels.</summary>
    public void BeginRename(string key)
    {
        int i = _items.FindIndex(x => x.Key == key && x.CanRename);
        if (i < 0) return;
        var r = RowRect(i);
        _rename?.Dispose();
        var box = _rename = new TextBox
        {
            Text = _items[i].Text, BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.Field, ForeColor = Theme.Text, Font = Theme.Ui(S(15)),
        };
        box.SetBounds(r.X + S(36), r.Y + (r.Height - box.PreferredHeight) / 2, r.Width - S(44), box.PreferredHeight);
        bool done = false;
        void Finish(bool commit)
        {
            if (done) return;
            done = true;
            var text = box.Text;
            BeginInvoke(() => { Controls.Remove(box); box.Dispose(); if (_rename == box) _rename = null; Focus(); });
            if (commit && text.Trim() != "" && text != _items[i].Text) Renamed?.Invoke(key, text.Trim());
        }
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Finish(true); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { Finish(false); e.SuppressKeyPress = true; }
        };
        box.LostFocus += (_, _) => Finish(true);
        Controls.Add(box);
        box.Focus();
        box.SelectAll();
    }

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        int i = IndexAt(PointToClient(new Point(e.X, e.Y)));
        bool ok = e.Data?.GetDataPresent(typeof(string[])) == true && i >= 0 && _items[i].CanDrop;
        e.Effect = ok ? DragDropEffects.Move : DragDropEffects.None;
        int drop = ok ? i : -1;
        if (drop != _drop) { _drop = drop; Invalidate(); }
    }

    protected override void OnDragLeave(EventArgs e)
    {
        base.OnDragLeave(e);
        _drop = -1;
        Invalidate();
    }

    protected override void OnDragDrop(DragEventArgs e)
    {
        base.OnDragDrop(e);
        int i = _drop;
        _drop = -1;
        Invalidate();
        if (i >= 0 && e.Data?.GetData(typeof(string[])) is string[] ids) Dropped?.Invoke(_items[i].Key, ids);
    }
}

/// <summary>
/// The bookmark list: two-line rows (name, then who · session · when · folder) with Claude's bullets
/// (a hollow circle for you, the coral dot for Claude), rounded hover and selection, a slim scrollbar,
/// multi-select (Ctrl / Shift), keyboard navigation, dragging, and inline renaming.
/// </summary>
sealed class BookmarkList : ThemedControl
{
    List<Bookmark> _rows = [];
    readonly HashSet<string> _selected = [];
    int _focus = -1, _anchor = -1, _hover = -1, _scroll;
    Point? _dragFrom;
    bool _draggingThumb;
    int _thumbGrab;
    TextBox? _rename;

    public string EmptyTitle = "", EmptyHint = "";
    public event Action<Bookmark>? Open;
    public event Action<IReadOnlyList<string>>? DeleteRequested;
    public event Action<string, string>? Renamed;
    public event Action<Point>? ContextRequested;

    public BookmarkList()
    {
        SetStyle(ControlStyles.Selectable, true);
        TabStop = true;
        BackColor = Theme.Main;
    }

    int RowHeight => S(66);
    int Content => _rows.Count * RowHeight + S(8);
    int MaxScroll => Math.Max(0, Content - Height);

    public IReadOnlyList<Bookmark> SelectedBookmarks => _rows.Where(b => _selected.Contains(b.Id)).ToList();

    public void SetRows(List<Bookmark> rows)
    {
        var focusId = _focus >= 0 && _focus < _rows.Count ? _rows[_focus].Id : null;
        _rows = rows;
        _selected.IntersectWith(rows.Select(r => r.Id));
        _focus = focusId == null ? -1 : rows.FindIndex(r => r.Id == focusId);
        _scroll = Math.Clamp(_scroll, 0, MaxScroll);
        Invalidate();
    }

    Rectangle RowRect(int i) => new(S(16), S(4) + i * RowHeight - _scroll, Width - S(36), RowHeight - S(6));

    int IndexAt(Point p)
    {
        int i = (p.Y + _scroll - S(4)) / RowHeight;
        return p.Y + _scroll - S(4) >= 0 && i < _rows.Count ? i : -1;
    }

    Rectangle ThumbRect()
    {
        if (MaxScroll == 0) return Rectangle.Empty;
        int track = Height - S(8);
        int length = Math.Max(S(40), track * Height / Content);
        int top = S(4) + (track - length) * _scroll / MaxScroll;
        return new Rectangle(Width - S(12), top, S(6), length);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        g.Clear(Theme.Main);
        if (_rows.Count == 0)
        {
            using var title = Theme.Ui(S(17), semibold: true);
            using var hint = Theme.Ui(S(14));
            Draw(g, EmptyTitle, title, new Rectangle(0, Height / 2 - S(36), Width, S(30)), Theme.Text, center: true);
            Draw(g, EmptyHint, hint, new Rectangle(0, Height / 2, Width, S(24)), Theme.Dim, center: true);
            return;
        }
        using var name = Theme.Ui(S(15.5f));
        using var meta = Theme.Ui(S(13.5f));
        int first = Math.Max(0, _scroll / RowHeight), last = Math.Min(_rows.Count - 1, (_scroll + Height) / RowHeight);
        for (int i = first; i <= last; i++)
        {
            var b = _rows[i];
            var r = RowRect(i);
            bool selected = _selected.Contains(b.Id);
            if (selected)
            {
                Theme.FillRounded(g, Theme.Card, r, S(10));
                Theme.DrawRounded(g, Theme.CardBorder, Math.Max(1, S(1)), new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(10));
            }
            else if (i == _hover) Theme.FillRounded(g, Theme.Hover, r, S(10));
            if (i == _focus && Focused && !selected)
                Theme.DrawRounded(g, Theme.CardBorder, Math.Max(1, S(1)), new RectangleF(r.X + 0.5f, r.Y + 0.5f, r.Width - 1, r.Height - 1), S(10));

            // Claude's bullets: hollow for you, the coral dot for Claude.
            float d = S(9), cx = r.X + S(18), cy = r.Y + S(21);
            if (b.FromUser)
                using (var pen = new Pen(Theme.Dim, Math.Max(1.2f, S(1.5f)))) g.DrawEllipse(pen, cx - d / 2, cy - d / 2, d, d);
            else
                using (var dot = new SolidBrush(Theme.Accent)) g.FillEllipse(dot, cx - d / 2, cy - d / 2, d, d);

            int left = r.X + S(38), width = r.Width - S(52);
            Draw(g, b.Name, name, new Rectangle(left, r.Y + S(8), width, S(26)), selected ? Theme.Text : Theme.Text);
            var when = DateTimeOffset.FromUnixTimeMilliseconds(b.AddedAt).LocalDateTime.ToString("MMM d, HH:mm");
            var details = $"{(b.FromUser ? "You" : "Claude")}  ·  {b.SessionTitle}  ·  {when}{(b.Folder == "" ? "" : "  ·  " + b.Folder)}";
            Draw(g, details, meta, new Rectangle(left, r.Y + S(33), width, S(22)), Theme.Dim);
        }
        var thumb = ThumbRect();
        if (!thumb.IsEmpty) Theme.FillRounded(g, _draggingThumb ? Theme.Dim : Theme.Scrollbar, thumb, S(3));
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        ScrollTo(_scroll - e.Delta * RowHeight / 120);
    }

    void ScrollTo(int value)
    {
        int v = Math.Clamp(value, 0, MaxScroll);
        if (v == _scroll) return;
        _scroll = v;
        _rename?.Parent?.Focus();
        Invalidate();
    }

    void Reveal(int i)
    {
        if (i < 0) return;
        int top = S(4) + i * RowHeight, bottom = top + RowHeight;
        if (top < _scroll) ScrollTo(top - S(4));
        else if (bottom > _scroll + Height) ScrollTo(bottom - Height + S(4));
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        var thumb = ThumbRect();
        if (e.Button == MouseButtons.Left && !thumb.IsEmpty && e.X >= Width - S(16))
        {
            _draggingThumb = true;
            _thumbGrab = thumb.Contains(e.Location) ? e.Y - thumb.Y : thumb.Height / 2;
            DragThumb(e.Y);
            return;
        }
        int i = IndexAt(e.Location);
        if (i < 0) { if (e.Button == MouseButtons.Left) { _selected.Clear(); Invalidate(); } return; }
        var id = _rows[i].Id;
        if (e.Button == MouseButtons.Right)
        {
            if (!_selected.Contains(id)) { _selected.Clear(); _selected.Add(id); }
            _focus = i;
            Invalidate();
            ContextRequested?.Invoke(PointToScreen(e.Location));
            return;
        }
        if ((ModifierKeys & Keys.Shift) != 0 && _anchor >= 0)
        {
            _selected.Clear();
            for (int k = Math.Min(_anchor, i); k <= Math.Max(_anchor, i); k++) _selected.Add(_rows[k].Id);
        }
        else if ((ModifierKeys & Keys.Control) != 0)
        {
            if (!_selected.Remove(id)) _selected.Add(id);
            _anchor = i;
        }
        else
        {
            if (!_selected.Contains(id)) _selected.Clear();
            _selected.Add(id);
            _anchor = i;
            _dragFrom = e.Location;
        }
        _focus = i;
        Invalidate();
    }

    void DragThumb(int y)
    {
        int track = Height - S(8), length = ThumbRect().Height;
        if (track <= length) return;
        ScrollTo((y - _thumbGrab - S(4)) * MaxScroll / (track - length));
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draggingThumb) { DragThumb(e.Y); return; }
        if (_dragFrom is { } from && e.Button == MouseButtons.Left
            && (Math.Abs(e.X - from.X) > SystemInformation.DragSize.Width || Math.Abs(e.Y - from.Y) > SystemInformation.DragSize.Height))
        {
            _dragFrom = null;
            DoDragDrop(_selected.ToArray(), DragDropEffects.Move);
            return;
        }
        int hover = IndexAt(e.Location);
        if (hover != _hover) { _hover = hover; Invalidate(); }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _dragFrom = null;
        if (_draggingThumb) { _draggingThumb = false; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        Invalidate();
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        int i = IndexAt(e.Location);
        if (e.Button == MouseButtons.Left && i >= 0) Open?.Invoke(_rows[i]);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End or Keys.PageUp or Keys.PageDown || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_rows.Count == 0) return;
        int page = Math.Max(1, Height / RowHeight - 1);
        int next = e.KeyCode switch
        {
            Keys.Up => Math.Max(0, _focus - 1),
            Keys.Down => Math.Min(_rows.Count - 1, _focus + 1),
            Keys.Home => 0,
            Keys.End => _rows.Count - 1,
            Keys.PageUp => Math.Max(0, _focus - page),
            Keys.PageDown => Math.Min(_rows.Count - 1, _focus + page),
            _ => -1,
        };
        if (next >= 0)
        {
            if (!e.Shift || _anchor < 0) { _selected.Clear(); _anchor = next; }
            else _selected.Clear();
            for (int k = Math.Min(_anchor, next); k <= Math.Max(_anchor, next); k++) _selected.Add(_rows[k].Id);
            _focus = next;
            Reveal(next);
            Invalidate();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter && _focus >= 0) { Open?.Invoke(_rows[_focus]); e.Handled = true; }
        else if (e.KeyCode == Keys.Delete && _selected.Count > 0) { DeleteRequested?.Invoke(_selected.ToList()); e.Handled = true; }
        else if (e.KeyCode == Keys.F2) { BeginRename(); e.Handled = true; }
        else if (e.Control && e.KeyCode == Keys.A) { foreach (var r in _rows) _selected.Add(r.Id); Invalidate(); e.Handled = true; }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
    protected override void OnResize(EventArgs e) { base.OnResize(e); _scroll = Math.Clamp(_scroll, 0, MaxScroll); }

    /// <summary>Edits the focused (or only selected) bookmark's name in place; Enter or leaving commits, Esc cancels.</summary>
    public void BeginRename()
    {
        int i = _focus >= 0 && _selected.Contains(_rows[_focus].Id) ? _focus : _rows.FindIndex(r => _selected.Contains(r.Id));
        if (i < 0) return;
        Reveal(i);
        var b = _rows[i];
        var r = RowRect(i);
        _rename?.Dispose();
        var box = _rename = new TextBox { Text = b.Name, BorderStyle = BorderStyle.FixedSingle, BackColor = Theme.Field, ForeColor = Theme.Text, Font = Theme.Ui(S(15.5f)) };
        box.SetBounds(r.X + S(34), r.Y + S(6), r.Width - S(48), box.PreferredHeight);
        bool done = false;
        void Finish(bool commit)
        {
            if (done) return;
            done = true;
            var text = box.Text;
            BeginInvoke(() => { Controls.Remove(box); box.Dispose(); if (_rename == box) _rename = null; Focus(); });
            if (commit && text.Trim() != "" && text != b.Name) Renamed?.Invoke(b.Id, text.Trim());
        }
        box.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter) { Finish(true); e.SuppressKeyPress = true; }
            else if (e.KeyCode == Keys.Escape) { Finish(false); e.SuppressKeyPress = true; }
        };
        box.LostFocus += (_, _) => Finish(true);
        Controls.Add(box);
        box.Focus();
        box.SelectAll();
    }
}

/// <summary>The hairline between the sidebar and the list, with a wide grab area; dragging resizes the sidebar.</summary>
sealed class PaneSplitter : ThemedControl
{
    Control? _resized;
    int _grabX, _startWidth;
    bool _hot;
    public int MinWidth = 160, MaxWidth = 600;

    public PaneSplitter() { Cursor = Cursors.SizeWE; }

    public void Attach(Control resized) => _resized = resized;

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Main);
        using var pen = new Pen(_hot ? Theme.Bullet : Theme.Divider, Math.Max(1, S(1)));
        e.Graphics.DrawLine(pen, 0, 0, 0, Height);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (_resized == null) return;
        _grabX = Cursor.Position.X;
        _startWidth = _resized.Width;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!Capture || _resized == null) return;
        _resized.Width = Math.Clamp(_startWidth + Cursor.Position.X - _grabX, S(MinWidth), S(MaxWidth));
    }
}

/// <summary>A small rounded chip button, like Claude's folder chip in its title bar.</summary>
sealed class Chip : ThemedControl
{
    bool _hot;
    public Chip() { Cursor = Cursors.Hand; }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        Prepare(g);
        g.Clear(Parent?.BackColor ?? Theme.Main);
        Theme.FillRounded(g, _hot ? Theme.Selected : Theme.CardBorder, new RectangleF(0, 0, Width, Height), S(7));
        using var font = Theme.Ui(S(13.5f));
        using var icons = Theme.Icons(S(10));
        var textWidth = TextRenderer.MeasureText(Text, font).Width;
        int x = (Width - textWidth - S(16)) / 2;
        Draw(g, Text, font, new Rectangle(x, 0, textWidth + S(2), Height), Theme.RowText);
        Draw(g, Theme.GlyphChevronDown, icons, new Rectangle(x + textWidth + S(2), 0, S(14), Height), Theme.Dim);
    }

    public int PreferredWidth()
    {
        using var font = Theme.Ui(S(13.5f));
        return TextRenderer.MeasureText(Text, font).Width + S(40);
    }

    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); Invalidate(); }
    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hot = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hot = false; Invalidate(); }
}
