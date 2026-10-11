using System.Runtime.InteropServices;
using static ClaudePowertoys.KeyboardHook;

namespace ClaudePowertoys;

/// <summary>Types into the focused window with injected key events, leaving the clipboard alone.</summary>
static class TextTyper
{
    /// <summary>
    /// A virtual key with no meaning, pressed while a held Alt is in use so that releasing Alt doesn't
    /// count as a lone Alt press, which opens Claude's menu bar.
    /// </summary>
    const int VK_NONE = 0xE8;

    /// <summary>Types the text as Unicode key events; each line break becomes Shift+Enter, so nothing is sent.</summary>
    public static void Type(string text)
    {
        var inputs = new List<INPUT>();
        foreach (var (line, i) in text.Replace("\r\n", "\n").Split('\n').Select((l, i) => (l, i)))
        {
            if (i > 0)
            {
                inputs.Add(Key(VK_SHIFT, up: false));
                inputs.Add(Key(VK_RETURN, up: false));
                inputs.Add(Key(VK_RETURN, up: true));
                inputs.Add(Key(VK_SHIFT, up: true));
            }
            foreach (var ch in line)
            {
                inputs.Add(Unicode(ch, up: false));
                inputs.Add(Unicode(ch, up: true));
            }
        }
        Send(inputs);
    }

    public static void MaskAltRelease() => Send([Key(VK_NONE, up: false), Key(VK_NONE, up: true)]);

    static void Send(List<INPUT> inputs)
    {
        if (inputs.Count == 0) return;
        if (SendInput((uint)inputs.Count, inputs.ToArray(), Marshal.SizeOf<INPUT>()) != inputs.Count)
            Log.Write($"Typing was cut short ({Marshal.GetLastWin32Error()})");
    }

    const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;

    static INPUT Key(int vk, bool up) => new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wVk = (ushort)vk, dwFlags = up ? KEYEVENTF_KEYUP : 0 } };
    static INPUT Unicode(char ch, bool up) => new() { type = INPUT_KEYBOARD, ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } };

    [StructLayout(LayoutKind.Sequential)]
    struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    // The union's largest member (MOUSEINPUT) sets INPUT's size; padding stands in for it.
    [StructLayout(LayoutKind.Sequential)]
    struct INPUT { public uint type; public KEYBDINPUT ki; public long padding; }

    [DllImport("user32.dll", SetLastError = true)] static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
