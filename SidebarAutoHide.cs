using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace ClaudePowertoys;

/// <summary>
/// True auto-hide for the Claude Desktop sidebar: while the sidebar is collapsed, touching the
/// window's left edge opens the app's own floating sidebar ("peek"), which overlays the main panel
/// without resizing it and closes by itself once the pointer leaves. A sidebar docked with the
/// app's "Show sidebar" button counts as pinned and is left alone.
///
/// The peek opens when the pointer enters the collapsed "Show sidebar" button, so the reveal posts
/// a WM_MOUSEMOVE over that button and then one at the real cursor position. The app itself is
/// never modified.
/// </summary>
sealed class SidebarAutoHide : IDisposable
{
    const int PollMs = 25;
    const int EdgePx = 12;
    // The app takes a moment to report the peek as open; don't re-post hovers inside that window.
    const int PeekSettleMs = 300;

    static readonly Condition ButtonCondition = new OrCondition(
        new PropertyCondition(AutomationElement.NameProperty, "Show sidebar", PropertyConditionFlags.IgnoreCase),
        new PropertyCondition(AutomationElement.NameProperty, "Hide sidebar", PropertyConditionFlags.IgnoreCase));

    readonly Thread _thread;
    volatile bool _stop;

    IntPtr _hwnd;
    AutomationElement? _button;
    long _lastPeekAt;
    // Where the cursor was at the last peek: a cursor parked there doesn't reopen a peek closed with Esc.
    Native.POINT? _lastPeekPt;

    public volatile bool Enabled;

    public SidebarAutoHide() => _thread = new Thread(Loop) { IsBackground = true, Name = "SidebarAutoHide" };

    public void Start() => _thread.Start();

    public void Dispose() => _stop = true;

    void Loop()
    {
        while (!_stop)
        {
            Thread.Sleep(PollMs);
            if (!Enabled) continue;
            try
            {
                Tick();
            }
            catch (ElementNotAvailableException)
            {
                _button = null;
            }
            catch (Exception ex)
            {
                Log.Write($"Tick failed: {ex.GetType().Name}: {ex.Message}");
                _button = null;
                Thread.Sleep(1000);
            }
        }
    }

    void Tick()
    {
        if (_hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd))
        {
            _hwnd = FindClaudeWindow();
            _button = null;
            if (_hwnd == IntPtr.Zero) return;
            Log.Write($"Claude window found: 0x{_hwnd:X}");
        }

        Native.GetCursorPos(out var pt);
        bool overClaude = Native.GetAncestor(Native.WindowFromPoint(pt), Native.GA_ROOT) == _hwnd;
        if (!overClaude || pt.X > Native.VisibleBounds(_hwnd).Left + EdgePx)
        {
            _lastPeekPt = null;
            return;
        }

        long now = Environment.TickCount64;
        if (now - _lastPeekAt < PeekSettleMs) return;
        if (_lastPeekPt is { } last && last.X == pt.X && last.Y == pt.Y) return;

        _button ??= AutomationElement.FromHandle(_hwnd).FindFirst(TreeScope.Descendants, ButtonCondition);
        if (_button == null) return;
        // "Hide sidebar" means the sidebar is docked (pinned), so there is nothing to reveal.
        if (_button.Current.Name.StartsWith("Hide", StringComparison.OrdinalIgnoreCase)) return;
        if (IsExpanded(_button)) return;

        var b = _button.Current.BoundingRectangle;
        Log.Write($"Peek sidebar (cursor {pt.X},{pt.Y}, button {b})");
        Native.PostMouseMove(_hwnd, (int)(b.Left + b.Width / 2), (int)(b.Top + b.Height / 2));
        Native.PostMouseMove(_hwnd, pt.X, pt.Y);
        _lastPeekAt = now;
        _lastPeekPt = pt;
    }

    /// <summary>While collapsed, the button's aria-expanded reports whether the floating sidebar is open.</summary>
    static bool IsExpanded(AutomationElement button) =>
        button.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out var ec)
        && ((ExpandCollapsePattern)ec).Current.ExpandCollapseState == ExpandCollapseState.Expanded;

    static IntPtr FindClaudeWindow()
    {
        foreach (var p in Process.GetProcessesByName("claude"))
        {
            using (p)
            {
                var hwnd = p.MainWindowHandle;
                if (hwnd != IntPtr.Zero && Native.ClassName(hwnd) == "Chrome_WidgetWin_1") return hwnd;
            }
        }
        return IntPtr.Zero;
    }
}

static class Native
{
    public const uint GA_ROOT = 2;
    const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr hwnd, System.Text.StringBuilder name, int max);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int size);
    [DllImport("user32.dll")] static extern bool ScreenToClient(IntPtr hwnd, ref POINT pt);
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    const uint WM_MOUSEMOVE = 0x0200;

    /// <summary>Tells the window the pointer is at this screen point, without moving the real cursor.</summary>
    public static void PostMouseMove(IntPtr hwnd, int screenX, int screenY)
    {
        var p = new POINT { X = screenX, Y = screenY };
        ScreenToClient(hwnd, ref p);
        PostMessage(hwnd, WM_MOUSEMOVE, IntPtr.Zero, (IntPtr)((p.Y << 16) | (p.X & 0xFFFF)));
    }

    public static string ClassName(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    /// <summary>Window bounds without the invisible resize border (a maximized window's GetWindowRect starts at -8/-16).</summary>
    public static RECT VisibleBounds(IntPtr hwnd)
    {
        DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out var r, Marshal.SizeOf<RECT>());
        return r;
    }
}
