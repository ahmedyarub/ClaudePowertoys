using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ClaudePowertoys;

/// <summary>
/// Claude Desktop's dark look, with colours sampled from its window: the sidebar and main surfaces,
/// fields, the selected row, cards, text levels, the hollow bullets and the coral accent. Claude's
/// own UI font (Anthropic Sans) is licensed for the app only, so Inter (SIL OFL) stands in when it is
/// installed, and Segoe UI Variable otherwise.
/// </summary>
static class Theme
{
    public static readonly Color Sidebar = Color.FromArgb(0x11, 0x11, 0x11);
    public static readonly Color Main = Color.FromArgb(0x15, 0x15, 0x15);
    public static readonly Color Field = Color.FromArgb(0x1D, 0x1D, 0x1D);
    public static readonly Color FieldBorder = Color.FromArgb(0x2A, 0x2A, 0x2A);
    public static readonly Color Hover = Color.FromArgb(0x1E, 0x1E, 0x1E);
    public static readonly Color Selected = Color.FromArgb(0x34, 0x34, 0x34);
    public static readonly Color Card = Color.FromArgb(0x21, 0x21, 0x21);
    public static readonly Color CardBorder = Color.FromArgb(0x2D, 0x2D, 0x2D);
    public static readonly Color Divider = Color.FromArgb(0x24, 0x24, 0x24);
    public static readonly Color Text = Color.FromArgb(0xF0, 0xEF, 0xEC);
    public static readonly Color RowText = Color.FromArgb(0xC3, 0xC2, 0xB7);
    public static readonly Color Dim = Color.FromArgb(0x89, 0x87, 0x81);
    public static readonly Color Bullet = Color.FromArgb(0x4D, 0x4C, 0x49);
    public static readonly Color Accent = Color.FromArgb(0xD9, 0x77, 0x57);
    public static readonly Color Scrollbar = Color.FromArgb(0x3A, 0x3A, 0x38);

    static readonly HashSet<string> Installed = new System.Drawing.Text.InstalledFontCollection().Families.Select(f => f.Name).ToHashSet();
    static readonly bool HasInter = Installed.Contains("Inter") && Installed.Contains("Inter SemiBold");

    /// <summary>Text in pixels (callers scale for DPI).</summary>
    public static Font Ui(float px, bool semibold = false) =>
        HasInter ? new(semibold ? "Inter SemiBold" : "Inter", px, GraphicsUnit.Pixel)
            : new(semibold ? "Segoe UI Variable Text Semibold" : "Segoe UI Variable Text", px, GraphicsUnit.Pixel);

    static readonly string IconFamily = Installed.Contains("Segoe Fluent Icons") ? "Segoe Fluent Icons" : "Segoe MDL2 Assets";

    /// <summary>Windows' line icons, the closest match to Claude's thin icons.</summary>
    public static Font Icons(float px) => new(IconFamily, px, GraphicsUnit.Pixel);

    public const string GlyphStar = "", GlyphFolder = "", GlyphDocument = "", GlyphSearch = "",
        GlyphAdd = "", GlyphChevronDown = "";

    public static GraphicsPath Rounded(RectangleF r, float radius)
    {
        var path = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { path.AddRectangle(r); return path; }
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, Color color, RectangleF r, float radius)
    {
        using var brush = new SolidBrush(color);
        using var path = Rounded(r, radius);
        g.FillPath(brush, path);
    }

    public static void DrawRounded(Graphics g, Color color, float width, RectangleF r, float radius)
    {
        using var pen = new Pen(color, width);
        using var path = Rounded(r, radius);
        g.DrawPath(pen, path);
    }

    /// <summary>A dark title bar and border in the given colours, with Windows 11's rounded corners.</summary>
    public static void DarkWindow(IntPtr hwnd, Color caption)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        int captionColor = ColorRef(caption), border = ColorRef(Divider), round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_CAPTION_COLOR, ref captionColor, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(int));
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    /// <summary>Windows 11's rounded corners on a popup (menus, lists).</summary>
    public static void RoundCorners(IntPtr hwnd)
    {
        int round = DWMWCP_ROUNDSMALL;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
    }

    static int ColorRef(Color c) => c.R | (c.G << 8) | (c.B << 16);

    const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20, DWMWA_WINDOW_CORNER_PREFERENCE = 33, DWMWA_BORDER_COLOR = 34, DWMWA_CAPTION_COLOR = 35;
    const int DWMWCP_ROUND = 2, DWMWCP_ROUNDSMALL = 3;

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
