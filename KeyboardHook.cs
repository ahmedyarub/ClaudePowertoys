using System.Runtime.InteropServices;

namespace ClaudePowertoys;

/// <summary>
/// A low-level keyboard hook. The handler runs on the thread that created the hook (the UI thread)
/// and must stay quick: Windows silently drops hooks that are slow. Returning true swallows the key.
/// </summary>
sealed class KeyboardHook : IDisposable
{
    public const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, WM_SYSKEYDOWN = 0x104, WM_SYSKEYUP = 0x105;
    public const int VK_TAB = 0x09, VK_RETURN = 0x0D, VK_ESCAPE = 0x1B, VK_SHIFT = 0x10, VK_CONTROL = 0x11, VK_MENU = 0x12,
        VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_LCONTROL = 0xA2, VK_RCONTROL = 0xA3;
    const int WH_KEYBOARD_LL = 13;

    public delegate bool Handler(int message, int vk);

    readonly Handler _handler;
    readonly HookProc _proc;
    readonly IntPtr _hook;

    public KeyboardHook(string name, Handler handler)
    {
        _handler = handler;
        _proc = Callback;
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) Log.Write($"{name}: keyboard hook failed ({Marshal.GetLastWin32Error()})");
    }

    public void Dispose()
    {
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
    }

    IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && _handler((int)wParam, Marshal.ReadInt32(lParam)))
            return 1;
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public static bool IsDown(int message) => message is WM_KEYDOWN or WM_SYSKEYDOWN;
    public static bool IsUp(int message) => message is WM_KEYUP or WM_SYSKEYUP;
    public static bool KeyHeld(int vk) => GetAsyncKeyState(vk) < 0;

    delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr SetWindowsHookEx(int id, HookProc proc, IntPtr module, uint threadId);
    [DllImport("user32.dll")] static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] static extern short GetAsyncKeyState(int vk);
}
