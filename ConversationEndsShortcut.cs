using System.Windows.Automation;
using static ClaudePowertoys.KeyboardHook;

namespace ClaudePowertoys;

/// <summary>
/// Ctrl+Home / Ctrl+End in Claude scroll to the beginning / end of the conversation. The app keeps
/// only part of a long conversation loaded, so its "Load earlier messages" / "Load later messages"
/// buttons are pressed until there is nothing more that way, then the conversation is scrolled to
/// the top or bottom. Left alone in other text fields (the terminal, the Files editor); in the prompt
/// box they scroll instead of moving the cursor.
/// </summary>
sealed class ConversationEndsShortcut : IDisposable
{
    const int VK_HOME = 0x24, VK_END = 0x23;
    const int MaxLoadPresses = 60;

    readonly KeyboardHook _hook;
    int _busy;

    public volatile bool Enabled;

    public ConversationEndsShortcut() =>
        _hook = new KeyboardHook("Conversation ends", (message, vk) => Enabled && Handle(message, vk));

    public void Dispose() => _hook.Dispose();

    bool Handle(int message, int vk)
    {
        if (vk is not (VK_HOME or VK_END) || !KeyHeld(VK_CONTROL)) return false;
        if (KeyHeld(VK_SHIFT) || KeyHeld(VK_MENU) || KeyHeld(VK_LWIN) || KeyHeld(VK_RWIN)) return false;
        if (!ClaudeWindow.IsInFront() || FocusTracker.InOtherTextField) return false;

        if (IsDown(message) && Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
        {
            bool toStart = vk == VK_HOME;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Go(toStart); }
                catch (Exception ex) { Log.Write($"Ctrl+{(toStart ? "Home" : "End")} failed: {ex.GetType().Name}: {ex.Message}"); }
                finally { Interlocked.Exchange(ref _busy, 0); }
            });
        }
        return true;
    }

    static void Go(bool toStart)
    {
        var window = AutomationElement.FromHandle(ClaudeWindow.Find());
        int presses = 0;
        while (presses < MaxLoadPresses && ConversationView.Press(window, toStart ? ConversationView.LoadEarlier : ConversationView.LoadLater))
        {
            presses++;
            Thread.Sleep(500);
        }
        ConversationView.ScrollToPercent(window, toStart ? 0 : 100);
        Log.Write($"Ctrl+{(toStart ? "Home" : "End")}: scrolled to the {(toStart ? "beginning" : "end")} after {presses} load press(es)");
    }
}
