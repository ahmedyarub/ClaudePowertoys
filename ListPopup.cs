using System.Runtime.InteropServices;

namespace ClaudePowertoys;

/// <summary>
/// A dark list with one selected row, shown above Claude's prompt box without taking focus; the
/// keyboard stays with a hook that moves the selection. Shows a window of rows around the selection.
/// </summary>
sealed class ListPopup : Form
{
    const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000, WS_EX_TOPMOST = 0x8;
    const uint SWP_NOACTIVATE = 0x10;
    static readonly IntPtr HWND_TOPMOST = new(-1);

    // Logical pixels, scaled by the DPI of Claude's monitor.
    const int WidthLogical = 760, RowLogical = 34, PadLogical = 12, HeaderLogical = 30, VisibleRows = 12;

    static readonly Color Background = Color.FromArgb(38, 38, 36);
    static readonly Color Border = Color.FromArgb(70, 70, 66);
    static readonly Color Highlight = Color.FromArgb(217, 119, 87);
    static readonly Color RowText = Color.FromArgb(240, 238, 230);
    static readonly Color DimText = Color.FromArgb(160, 158, 150);

    readonly string _title;
    List<string> _rows = [];
    int _selected;
    float _scale = 1;

    public ListPopup(string title)
    {
        _title = title;
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

    /// <param name="bottom">Screen y the popup's bottom edge sits on (the prompt box's top); centered on Claude when null.</param>
    public void ShowList(IntPtr claude, int? bottom, List<string> rows, int selected)
    {
        _rows = rows;
        _selected = selected;
        _scale = (claude != IntPtr.Zero ? GetDpiForWindow(claude) : 96) / 96f;

        int width = S(WidthLogical);
        int height = S(HeaderLogical) + Math.Clamp(rows.Count, 1, VisibleRows) * S(RowLogical) + S(PadLogical) * 2;
        var area = Native.VisibleBounds(claude);
        int cx = (area.Left + area.Right) / 2;
        int top = bottom is { } b ? b - height - S(8) : (area.Top + area.Bottom - height) / 2;

        SetWindowPos(Handle, HWND_TOPMOST, cx - width / 2, Math.Max(area.Top, top), width, height, SWP_NOACTIVATE);
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
        using (var border = new Pen(Border, Math.Max(1, S(1))))
            g.DrawRectangle(border, 0, 0, Width - 1, Height - 1);

        using var font = new Font("Segoe UI", 14 * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
        using var dimFont = new Font("Segoe UI", 12 * _scale, FontStyle.Regular, GraphicsUnit.Pixel);
        int pad = S(PadLogical), row = S(RowLogical);
        var flags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine;

        var header = _rows.Count == 0 ? $"{_title}: none yet" : $"{_title}  (↑ older, ↓ newer, Esc cancels)";
        TextRenderer.DrawText(g, header, dimFont, new Rectangle(pad, pad, Width - pad * 2, S(HeaderLogical) - pad / 2), DimText, flags);

        // A window of rows that keeps the selection in view.
        int first = Math.Clamp(_selected - VisibleRows / 2, 0, Math.Max(0, _rows.Count - VisibleRows));
        int y = pad + S(HeaderLogical) - pad / 2;
        for (int i = first; i < _rows.Count && i < first + VisibleRows; i++, y += row)
        {
            var rect = new Rectangle(pad / 2, y, Width - pad, row);
            if (i == _selected)
            {
                using var fill = new SolidBrush(Color.FromArgb(70, Highlight));
                using var edge = new Pen(Highlight, Math.Max(1, S(2)));
                g.FillRectangle(fill, rect);
                g.DrawRectangle(edge, rect.X, rect.Y, rect.Width - 1, rect.Height - 1);
            }
            TextRenderer.DrawText(g, _rows[i], font, new Rectangle(rect.X + pad, y, rect.Width - pad * 2, row), RowText, flags);
        }
    }

    [DllImport("user32.dll")] static extern uint GetDpiForWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
}
