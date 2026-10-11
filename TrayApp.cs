using System.Drawing.Drawing2D;

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
    readonly SidebarAutoHide _autoHide;
    readonly GlobalHotkey _needsInputHotkey;

    public TrayApp()
    {
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
        _switcher.Enabled = _settings.SessionSwitcher;
        _switcher.RecentWindow = TimeSpan.FromHours(_settings.RecentSessionHours);
        _sendNow.Enabled = _settings.CtrlEnterSendsNow;
        _outline.Enabled = _settings.OutlineButton;
        _outline.Start();
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
        _needsInputHotkey.Dispose();
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
