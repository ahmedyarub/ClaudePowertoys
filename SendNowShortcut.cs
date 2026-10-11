using System.Windows.Automation;
using static ClaudePowertoys.KeyboardHook;

namespace ClaudePowertoys;

/// <summary>
/// Ctrl+Enter in Claude always sends now. The key still reaches the app, which sends or queues a
/// typed message as usual; if a queued message then offers "Send now" (stop the current reply and
/// send it next), that button is pressed through UI Automation. This also covers a message that
/// was already queued: Ctrl+Enter with an empty prompt box sends it now. When nothing is queued
/// (Claude was idle, or the app already sent it straight away) no button shows up and nothing
/// else happens. Ctrl+Enter inside another text field (the Files editor, a dialog) is left alone.
/// </summary>
sealed class SendNowShortcut : IDisposable
{
    const int WatchMs = 2000;
    const int PollMs = 100;

    static readonly Condition PromptCondition = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
        new PropertyCondition(AutomationElement.NameProperty, "Prompt"));

    static readonly Condition SendNowCondition = new AndCondition(
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
        new PropertyCondition(AutomationElement.NameProperty, "Send now", PropertyConditionFlags.IgnoreCase));

    readonly KeyboardHook _hook;
    int _watching;

    public volatile bool Enabled;

    public SendNowShortcut() =>
        _hook = new KeyboardHook("Send now shortcut", (message, vk) => Enabled && Handle(message, vk));

    public void Dispose() => _hook.Dispose();

    bool Handle(int message, int vk)
    {
        if (vk == VK_RETURN && IsDown(message) && KeyHeld(VK_CONTROL)
            && !KeyHeld(VK_SHIFT) && !KeyHeld(VK_MENU) && ClaudeWindow.IsInFront()
            && Interlocked.CompareExchange(ref _watching, 1, 0) == 0)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try { PressSendNow(); }
                catch (Exception ex) { Log.Write($"Send now failed: {ex.GetType().Name}: {ex.Message}"); }
                finally { Interlocked.Exchange(ref _watching, 0); }
            });
        }
        return false;
    }

    static void PressSendNow()
    {
        // The Enter is still on its way to the app, so focus is where the person typed.
        var focused = AutomationElement.FocusedElement;
        if (focused != null && focused.Current.ControlType is var type
            && (type == ControlType.Edit || type == ControlType.Document) && focused.Current.Name != "Prompt")
            return;

        var window = AutomationElement.FromHandle(ClaudeWindow.Find());
        long start = Environment.TickCount64;
        while (Environment.TickCount64 - start < WatchMs)
        {
            Thread.Sleep(PollMs);
            var button = window.FindFirst(TreeScope.Descendants, SendNowCondition);
            if (button == null) continue;
            if (button.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            {
                ((InvokePattern)invoke).Invoke();
                Log.Write($"Ctrl+Enter: pressed Send now after {Environment.TickCount64 - start} ms");
                // Invoking moves keyboard focus to the button; hand it back to the prompt box.
                try { window.FindFirst(TreeScope.Descendants, PromptCondition)?.SetFocus(); }
                catch (Exception ex) { Log.Write($"Ctrl+Enter: refocusing the prompt box failed: {ex.Message}"); }
            }
            else
            {
                Log.Write("Ctrl+Enter: Send now button exposes no Invoke pattern");
            }
            return;
        }

        // Either nothing was queued, or the button is named differently: list what there is to tell which.
        var names = window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button))
            .Cast<AutomationElement>()
            .Select(b => b.Current.Name)
            .Where(n => n.Contains("send", StringComparison.OrdinalIgnoreCase) || n.Contains("queue", StringComparison.OrdinalIgnoreCase));
        Log.Write($"Ctrl+Enter: no Send now within {WatchMs} ms (send/queue buttons: {string.Join(", ", names.DefaultIfEmpty("none"))})");
    }
}
