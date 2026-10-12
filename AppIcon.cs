using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace ClaudePowertoys;

/// <summary>
/// The app's icon: a six-ray spark in Claude's coral on a transparent background, drawn at the exact
/// size it is shown at so it stays crisp. A count of sessions waiting appears as a small light badge
/// with a dark ring, in the lower right.
/// </summary>
static class AppIcon
{
    const int SM_CXSMICON = 49;

    /// <summary>The notification area's icon size for the current DPI.</summary>
    public static int TraySize => GetSystemMetricsForDpi(SM_CXSMICON, GetDpiForSystem());

    public static Icon Draw(int size, int waiting = 0)
    {
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            float c = size / 2f, length = size * 0.43f;
            using var pen = new Pen(Theme.Accent, size * 0.15f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            for (int i = 0; i < 3; i++)
            {
                double a = Math.PI / 2 + i * Math.PI / 3;
                float dx = (float)Math.Cos(a) * length, dy = (float)Math.Sin(a) * length;
                g.DrawLine(pen, c - dx, c - dy, c + dx, c + dy);
            }

            if (waiting > 0)
            {
                float d = size * 0.54f, x = size - d, y = size - d;
                using (var ring = new SolidBrush(Theme.Sidebar)) g.FillEllipse(ring, x - size * 0.06f, y - size * 0.06f, d + size * 0.12f, d + size * 0.12f);
                using (var badge = new SolidBrush(Theme.Text)) g.FillEllipse(badge, x, y, d, d);
                using var font = Theme.Ui(d * (waiting > 9 ? 0.5f : 0.68f), semibold: true);
                using var format = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
                using var text = new SolidBrush(Theme.Sidebar);
                g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                g.DrawString(waiting > 9 ? "9+" : waiting.ToString(), font, text, new RectangleF(x, y + d * 0.03f, d, d), format);
            }
        }
        var handle = bmp.GetHicon();
        // Icon.FromHandle doesn't own the handle; a clone does, so the caller can dispose it normally.
        using var borrowed = Icon.FromHandle(handle);
        var icon = (Icon)borrowed.Clone();
        DestroyIcon(handle);
        return icon;
    }

    [DllImport("user32.dll")] static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll")] static extern uint GetDpiForSystem();
    [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr icon);
}
