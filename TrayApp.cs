using System.Drawing.Drawing2D;

namespace ClaudePowertoys;

/// <summary>Notification-area icon and its right-click menu.</summary>
sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon _icon;
    readonly Settings _settings = Settings.Load();
    readonly ResizeHandleBlocker _handleBlocker = new();
    readonly SidebarAutoHide _autoHide;

    public TrayApp()
    {
        _autoHide = new SidebarAutoHide(_handleBlocker.OverlayHandle);

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
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());

        _icon = new NotifyIcon
        {
            Icon = DrawIcon(),
            Text = "Claude Powertoys",
            ContextMenuStrip = menu,
            Visible = true,
        };

        _autoHide.Enabled = _settings.AutoHideSidebar;
        _autoHide.Start();
        _handleBlocker.Enabled = _settings.RemoveResizeHandle;
        _handleBlocker.Start();
        Log.Write($"Started, auto-hide sidebar {OnOff(_settings.AutoHideSidebar)}, remove resize handle {OnOff(_settings.RemoveResizeHandle)}");
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
        _icon.Visible = false;
        _icon.Dispose();
        Log.Write("Exited");
        base.ExitThreadCore();
    }

    static Icon DrawIcon()
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
        }
        return Icon.FromHandle(bmp.GetHicon());
    }
}
