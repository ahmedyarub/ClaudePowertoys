using System.Drawing.Drawing2D;

namespace ClaudePowertoys;

/// <summary>Notification-area icon and its right-click menu.</summary>
sealed class TrayApp : ApplicationContext
{
    readonly NotifyIcon _icon;
    readonly Settings _settings = Settings.Load();
    readonly SidebarAutoHide _autoHide = new();

    public TrayApp()
    {
        var autoHideItem = new ToolStripMenuItem("Auto-hide sidebar")
        {
            CheckOnClick = true,
            Checked = _settings.AutoHideSidebar,
        };
        autoHideItem.CheckedChanged += (_, _) =>
        {
            _settings.AutoHideSidebar = autoHideItem.Checked;
            _settings.Save();
            _autoHide.Enabled = autoHideItem.Checked;
            Log.Write($"Auto-hide sidebar {(autoHideItem.Checked ? "on" : "off")}");
        };

        var menu = new ContextMenuStrip();
        menu.Items.Add(autoHideItem);
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
        Log.Write($"Started, auto-hide sidebar {(_settings.AutoHideSidebar ? "on" : "off")}");
    }

    protected override void ExitThreadCore()
    {
        _autoHide.Dispose();
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
