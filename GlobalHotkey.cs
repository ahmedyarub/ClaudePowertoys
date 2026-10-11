using System.Runtime.InteropServices;

namespace ClaudePowertoys;

/// <summary>
/// A system-wide hotkey (RegisterHotKey), written like "Win+Shift+C". Fires on the UI thread.
/// Registration fails when another program already owns the combination; that is logged.
/// </summary>
sealed class GlobalHotkey : NativeWindow, IDisposable
{
    const int WM_HOTKEY = 0x0312;
    const uint MOD_ALT = 0x1, MOD_CONTROL = 0x2, MOD_SHIFT = 0x4, MOD_WIN = 0x8, MOD_NOREPEAT = 0x4000;

    static int _nextId = 1;

    readonly int _id = _nextId++;
    readonly Action _onPress;
    bool _registered;

    public string Text { get; }

    public GlobalHotkey(string text, Action onPress)
    {
        Text = text;
        _onPress = onPress;
        CreateHandle(new CreateParams());
    }

    public bool Enabled
    {
        get => _registered;
        set
        {
            if (value == _registered) return;
            if (!value)
            {
                UnregisterHotKey(Handle, _id);
                _registered = false;
                return;
            }
            if (!TryParse(Text, out var modifiers, out var key))
            {
                Log.Write($"Hotkey '{Text}' is not a valid combination");
                return;
            }
            _registered = RegisterHotKey(Handle, _id, modifiers | MOD_NOREPEAT, key);
            if (!_registered) Log.Write($"Hotkey {Text} is taken by another program ({Marshal.GetLastWin32Error()})");
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_HOTKEY && (int)m.WParam == _id) _onPress();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        Enabled = false;
        DestroyHandle();
    }

    static bool TryParse(string text, out uint modifiers, out uint key)
    {
        modifiers = 0;
        key = 0;
        foreach (var part in text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            switch (part.ToLowerInvariant())
            {
                case "win": modifiers |= MOD_WIN; break;
                case "ctrl" or "control": modifiers |= MOD_CONTROL; break;
                case "shift": modifiers |= MOD_SHIFT; break;
                case "alt": modifiers |= MOD_ALT; break;
                default:
                    if (key != 0 || !Enum.TryParse<Keys>(part, ignoreCase: true, out var k)) return false;
                    key = (uint)k;
                    break;
            }
        }
        return key != 0 && modifiers != 0;
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(IntPtr hwnd, int id);
}
