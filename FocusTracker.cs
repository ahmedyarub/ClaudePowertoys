using System.Windows.Automation;

namespace ClaudePowertoys;

/// <summary>
/// Keeps track, through UI Automation focus events, of whether keyboard focus is in a text field
/// other than Claude's prompt box (the terminal, the Files editor, a dialog). Keyboard hooks must
/// decide at once, and asking UI Automation from inside a hook is too slow.
/// </summary>
static class FocusTracker
{
    static int _started;
    static volatile bool _inOtherTextField;

    public static bool InOtherTextField
    {
        get
        {
            Start();
            return _inOtherTextField;
        }
    }

    static void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0) return;
        // UI Automation event handlers are registered off the UI thread.
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try { Automation.AddAutomationFocusChangedEventHandler(OnFocusChanged); }
            catch (Exception ex) { Log.Write($"Focus tracking failed: {ex.Message}"); }
        });
    }

    static void OnFocusChanged(object sender, AutomationFocusChangedEventArgs e)
    {
        try
        {
            var current = ((AutomationElement)sender).Current;
            _inOtherTextField = (current.ControlType == ControlType.Edit || current.ControlType == ControlType.Document)
                && current.Name != "Prompt";
        }
        catch (ElementNotAvailableException) { }
    }
}
