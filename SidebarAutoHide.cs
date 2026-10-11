using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;

namespace ClaudePowertoys;

/// <summary>
/// True auto-hide for the Claude Desktop sidebar: keeps the docked sidebar collapsed and, when the
/// mouse touches the window's left edge, opens the app's own floating sidebar ("peek"), which
/// overlays the main panel without resizing it and closes by itself once the pointer leaves.
///
/// The peek opens when the pointer enters the collapsed "Show sidebar" button, so the reveal posts
/// a WM_MOUSEMOVE over that button and then one at the real cursor position. The docked sidebar
/// is collapsed through UI Automation. The app itself is never modified.
/// </summary>
sealed class SidebarAutoHide : IDisposable
{
    const int PollMs = 100;
    const int EdgePx = 6;
    const int RevealDelayMs = 150;
    const int HideDelayMs = 600;

    static readonly Condition ButtonCondition = new OrCondition(
        new PropertyCondition(AutomationElement.NameProperty, "Show sidebar", PropertyConditionFlags.IgnoreCase),
        new PropertyCondition(AutomationElement.NameProperty, "Hide sidebar", PropertyConditionFlags.IgnoreCase));

    static readonly Condition SidebarCondition = new AndCondition(
        new PropertyCondition(AutomationElement.NameProperty, "Sidebar"),
        new PropertyCondition(AutomationElement.LocalizedControlTypeProperty, "complementary"));

    readonly Thread _thread;
    volatile bool _stop;

    IntPtr _hwnd;
    AutomationElement? _button;
    AutomationElement? _sidebar;
    long? _edgeSince;
    long? _outsideSince;
    // One peek per visit to the edge, so closing the peek (e.g. Esc) with the cursor parked there does not reopen it.
    bool _edgeArmed = true;

    public volatile bool Enabled;

    public SidebarAutoHide() => _thread = new Thread(Loop) { IsBackground = true, Name = "SidebarAutoHide" };

    public void Start() => _thread.Start();

    public void Dispose() => _stop = true;

    void Loop()
    {
        while (!_stop)
        {
            Thread.Sleep(PollMs);
            if (!Enabled)
            {
                _edgeSince = _outsideSince = null;
                continue;
            }
            try
            {
                Tick();
            }
            catch (ElementNotAvailableException)
            {
                _button = _sidebar = null;
            }
            catch (Exception ex)
            {
                Log.Write($"Tick failed: {ex.GetType().Name}: {ex.Message}");
                _button = _sidebar = null;
                Thread.Sleep(1000);
            }
        }
    }

    void Tick()
    {
        if (_hwnd == IntPtr.Zero || !Native.IsWindow(_hwnd))
        {
            _hwnd = FindClaudeWindow();
            _button = _sidebar = null;
            if (_hwnd == IntPtr.Zero) return;
            Log.Write($"Claude window found: 0x{_hwnd:X}");
        }

        _button ??= AutomationElement.FromHandle(_hwnd).FindFirst(TreeScope.Descendants, ButtonCondition);
        if (_button == null) return;

        bool docked = _button.Current.Name.StartsWith("Hide", StringComparison.OrdinalIgnoreCase);
        Native.GetCursorPos(out var pt);
        bool overClaude = Native.GetAncestor(Native.WindowFromPoint(pt), Native.GA_ROOT) == _hwnd;
        long now = Environment.TickCount64;

        if (!docked)
        {
            _sidebar = null;
            _outsideSince = null;
            bool atEdge = overClaude && pt.X <= Native.VisibleBounds(_hwnd).Left + EdgePx;
            if (!atEdge)
            {
                _edgeSince = null;
                _edgeArmed = true;
                return;
            }
            if (!_edgeArmed) return;
            _edgeSince ??= now;
            if (now - _edgeSince < RevealDelayMs) return;
            _edgeSince = null;
            _edgeArmed = false;
            if (IsExpanded(_button)) return;

            var b = _button.Current.BoundingRectangle;
            Log.Write($"Peek sidebar (cursor {pt.X},{pt.Y}, button {b})");
            Native.PostMouseMove(_hwnd, (int)(b.Left + b.Width / 2), (int)(b.Top + b.Height / 2));
            Native.PostMouseMove(_hwnd, pt.X, pt.Y);
            return;
        }

        // Docked open (the person pressed Show sidebar): collapse it once the mouse has left it.
        _edgeSince = null;
        _sidebar ??= AutomationElement.FromHandle(_hwnd).FindFirst(TreeScope.Descendants, SidebarCondition);
        if (_sidebar == null) return;

        var r = _sidebar.Current.BoundingRectangle;
        bool overSidebar = overClaude && pt.X >= r.Left && pt.X < r.Right && pt.Y >= r.Top && pt.Y < r.Bottom;
        // A held button means a drag (e.g. a session dragged out to split view): never hide mid-drag.
        if (overSidebar || Native.MouseButtonDown())
        {
            _outsideSince = null;
            return;
        }
        _outsideSince ??= now;
        if (now - _outsideSince < HideDelayMs) return;
        Log.Write($"Collapse docked sidebar (cursor {pt.X},{pt.Y}, sidebar {r})");
        if (_button.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) ((InvokePattern)invoke).Invoke();
        else Log.Write("'Hide sidebar' button exposes no Invoke pattern");
        _outsideSince = null;
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
    const int VK_LBUTTON = 0x01, VK_RBUTTON = 0x02;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT pt);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT pt);
    [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vKey);
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

    public static bool MouseButtonDown() => GetAsyncKeyState(VK_LBUTTON) < 0 || GetAsyncKeyState(VK_RBUTTON) < 0;

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
