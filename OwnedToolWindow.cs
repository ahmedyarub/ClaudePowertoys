using System.Runtime.InteropServices;

namespace ClaudePowertoys;

/// <summary>
/// A borderless window owned by Claude's window: it stays directly above Claude (and below anything
/// covering Claude), hides with it when minimized, never appears in the taskbar and never takes focus,
/// though it still gets mouse clicks.
/// </summary>
class OwnedToolWindow : Form
{
    const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
    const int WM_MOUSEACTIVATE = 0x21, MA_NOACTIVATE = 3;
    const int GWLP_HWNDPARENT = -8;
    const uint SWP_NOACTIVATE = 0x10, SWP_NOZORDER = 0x4;

    IntPtr _owner;
    Rectangle _bounds;

    public OwnedToolWindow()
    {
        Lifeline.Guard(this);
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
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

    /// <summary>Shows the window at <paramref name="bounds"/> (physical pixels, as UI Automation reports them), owned by <paramref name="owner"/>.</summary>
    public void Place(IntPtr owner, Rectangle bounds)
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

        // SetWindowPos rather than WinForms' Bounds, which would be rescaled across monitors with different DPI.
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
