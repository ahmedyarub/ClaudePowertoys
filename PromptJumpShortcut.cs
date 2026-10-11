using System.Windows.Automation;
using static ClaudePowertoys.KeyboardHook;

namespace ClaudePowertoys;

/// <summary>
/// Ctrl+Up / Ctrl+Down in Claude scroll the conversation to the previous / next prompt you typed.
/// Repeated presses step from the prompt last jumped to; otherwise from what is at the top of the
/// view. Going up past the oldest loaded prompt presses "Load earlier messages". Left alone in
/// other text fields (the terminal, the Files editor), where the keys have their own meaning.
/// </summary>
sealed class PromptJumpShortcut : IDisposable
{
    const int VK_UP = 0x26, VK_DOWN = 0x28;
    // A press this soon after the last jump steps from that prompt rather than from the view's top.
    const int ChainMs = 15000;
    const int MaxLoadPresses = 5;

    readonly KeyboardHook _hook;
    int _busy;
    AutomationElement? _last;
    long _lastAt;

    public volatile bool Enabled;

    public PromptJumpShortcut() =>
        _hook = new KeyboardHook("Prompt jump", (message, vk) => Enabled && Handle(message, vk));

    public void Dispose() => _hook.Dispose();

    bool Handle(int message, int vk)
    {
        if (vk is not (VK_UP or VK_DOWN) || !KeyHeld(VK_CONTROL)) return false;
        if (KeyHeld(VK_SHIFT) || KeyHeld(VK_MENU) || KeyHeld(VK_LWIN) || KeyHeld(VK_RWIN)) return false;
        if (!ClaudeWindow.IsInFront() || FocusTracker.InOtherTextField) return false;

        if (IsDown(message) && Interlocked.CompareExchange(ref _busy, 1, 0) == 0)
        {
            int step = vk == VK_UP ? -1 : 1;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { Jump(step); }
                catch (Exception ex) { Log.Write($"Prompt jump failed: {ex.GetType().Name}: {ex.Message}"); }
                finally { Interlocked.Exchange(ref _busy, 0); }
            });
        }
        return true;
    }

    void Jump(int step)
    {
        var window = AutomationElement.FromHandle(ClaudeWindow.Find());
        for (int loads = 0; loads <= MaxLoadPresses; loads++)
        {
            var headings = ConversationView.PromptHeadings(window);
            int target = Target(window, headings, step);
            if (target >= 0 && target < headings.Count)
            {
                ConversationView.ScrollTo(headings[target]);
                _last = headings[target];
                _lastAt = Environment.TickCount64;
                return;
            }
            // Past the newest prompt there is nothing to go to; past the oldest loaded one, load more.
            if (step > 0 || !ConversationView.Press(window, ConversationView.LoadEarlier)) return;
            Thread.Sleep(600);
        }
        Log.Write($"Prompt jump: no earlier prompt after {MaxLoadPresses} load presses");
    }

    int Target(AutomationElement window, List<AutomationElement> headings, int step)
    {
        if (_last != null && Environment.TickCount64 - _lastAt < ChainMs)
        {
            int current = headings.FindIndex(h => Automation.Compare(h, _last));
            if (current >= 0) return current + step;
        }

        // No recent jump: the prompt just above, or just below, the top of the view.
        var area = ConversationView.ScrollArea(window);
        if (area == null) return -1;
        double top = area.Value.Top + 4;
        return step < 0
            ? headings.FindLastIndex(h => h.Current.BoundingRectangle.Top < top)
            : headings.FindIndex(h => h.Current.BoundingRectangle.Top > top);
    }
}
