using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace ClaudePowertoys;

/// <summary>
/// Removes the Claude Desktop sidebar's resize handle from reach by covering it with an invisible
/// window that takes the mouse: the handle never sees the pointer, so it cannot be dragged, shows
/// no resize cursor, and cannot close the floating sidebar. Tracks the handle wherever the app puts
/// it (the left edge while collapsed, the sidebar's right edge while docked).
/// </summary>
sealed class ResizeHandleBlocker : IDisposable
{
    const int PollMs = 150;

    static readonly Condition HandleCondition = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Thumb),
        new PropertyCondition(AutomationElement.NameProperty, "Resize sidebar"));

    readonly Overlay _overlay = new();
    readonly Thread _thread;
    volatile bool _stop;

    IntPtr _hwnd;
    AutomationElement? _handle;

    public volatile bool Enabled;

    /// <summary>Created on the UI thread, with the tray.</summary>
    public ResizeHandleBlocker()
    {
        _ = _overlay.Handle;
        _thread = new Thread(Loop) { IsBackground = true, Name = "ResizeHandleBlocker" };
    }

    public IntPtr OverlayHandle => _overlay.Handle;

    public void Start() => _thread.Start();

    public void Dispose()
    {
        _stop = true;
        _overlay.Dispose();
    }

    void Loop()
    {
        while (!_stop)
        {
            Thread.Sleep(PollMs);
            try
            {
                if (Enabled && Tick()) continue;
            }
            catch (ElementNotAvailableException)
            {
                _handle = null;
            }
            catch (Exception ex)
            {
                Log.Write($"Resize handle blocker failed: {ex.GetType().Name}: {ex.Message}");
                _handle = null;
                Thread.Sleep(1000);
            }
            Post(() => _overlay.Conceal());
        }
    }

    /// <summary>Returns whether the overlay should be showing.</summary>
    bool Tick()
    {
        if (_hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd))
        {
            _hwnd = ClaudeWindow.Find();
            _handle = null;
            if (_hwnd == IntPtr.Zero) return false;
        }
        if (Native.IsIconic(_hwnd)) return false;

        _handle ??= AutomationElement.FromHandle(_hwnd).FindFirst(TreeScope.Descendants, HandleCondition);
        if (_handle == null) return false;

        var r = _handle.Current.BoundingRectangle;
        if (r.IsEmpty || r.Width <= 0 || r.Height <= 0) return false;

        var owner = _hwnd;
        var bounds = new Rectangle((int)r.X, (int)r.Y, (int)Math.Ceiling(r.Width), (int)Math.Ceiling(r.Height));
        Post(() => _overlay.Cover(owner, bounds));
        return true;
    }

    void Post(Action action)
    {
        if (_stop || _overlay.IsDisposed) return;
        try { _overlay.BeginInvoke(action); }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    /// <summary>An all-but-transparent window that swallows the mouse without ever taking focus.</summary>
    sealed class Overlay : Form
    {
        const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
        const int GWLP_HWNDPARENT = -8;
        const uint SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 0x4;

        IntPtr _owner;
        Rectangle _bounds;

        public Overlay()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Black;
            // Not 0: a fully transparent layered window can let the mouse through.
            Opacity = 0.01;
            Cursor = Cursors.Arrow;
        }

        protected override bool ShowWithoutActivation => true;

        protected override CreateParams CreateParams
        {
            get
            {
                var cp = base.CreateParams;
                cp.ExStyle |= WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_MOUSEACTIVATE)
            {
                m.Result = MA_NOACTIVATE;
                return;
            }
            base.WndProc(ref m);
        }

        /// <summary>Owned by Claude's window, so it stays directly above Claude and below anything covering Claude.</summary>
        public void Cover(IntPtr owner, Rectangle bounds)
        {
            bool show = !Visible;
            if (owner != _owner)
            {
                // Showing an owned window puts it directly above its owner; reshow under the new owner.
                if (Visible) Hide();
                SetWindowLongPtr(Handle, GWLP_HWNDPARENT, owner);
                _owner = owner;
                show = true;
            }
            if (!show && bounds == _bounds) return;

            // Bounds go through SetWindowPos in physical pixels, as UI Automation reports them;
            // WinForms' own Bounds would be rescaled across monitors with different DPI.
            SetWindowPos(Handle, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOACTIVATE | SWP_NOZORDER);
            _bounds = bounds;
            if (show) Show();
        }

        public void Conceal()
        {
            if (Visible) Hide();
        }

        [DllImport("user32.dll")] static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    }
}
