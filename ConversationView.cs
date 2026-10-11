using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace ClaudePowertoys;

/// <summary>
/// The conversation as Claude Desktop shows it, through UI Automation. Each prompt is preceded by an
/// offscreen heading "You said: &lt;its start&gt;". A long conversation is only partly loaded; the
/// "Load earlier messages" / "Load later messages" buttons bring in the rest.
/// </summary>
static class ConversationView
{
    public const string LoadEarlier = "Load earlier messages";
    public const string LoadLater = "Load later messages";
    const string HeadingPrefix = "You said: ";
    public const string ReplyPrefix = "Claude responded: ";

    static readonly Condition HeadingCondition = new PropertyCondition(AutomationElement.LocalizedControlTypeProperty, "heading");

    /// <summary>Every loaded message heading, yours ("You said: ") and Claude's ("Claude responded: "), in conversation order.</summary>
    public static List<AutomationElement> MessageHeadings(AutomationElement window) =>
        window.FindAll(TreeScope.Descendants, HeadingCondition).Cast<AutomationElement>()
            .Where(h => h.Current.Name.StartsWith(HeadingPrefix, StringComparison.Ordinal) || h.Current.Name.StartsWith(ReplyPrefix, StringComparison.Ordinal))
            .ToList();

    public static bool IsPrompt(AutomationElement heading) => heading.Current.Name.StartsWith(HeadingPrefix, StringComparison.Ordinal);

    /// <summary>The start of the message a heading stands for, either side.</summary>
    public static string MessageStart(AutomationElement heading)
    {
        var name = heading.Current.Name;
        return name.StartsWith(HeadingPrefix, StringComparison.Ordinal) ? name[HeadingPrefix.Length..] : name[ReplyPrefix.Length..];
    }

    /// <summary>A heading shows the start of a message; it matches text that begins with it (both normalized).</summary>
    public static bool Matches(string text, string heading)
    {
        heading = heading.TrimEnd('…').TrimEnd();
        return heading.Length > 0 && text.StartsWith(heading, StringComparison.Ordinal);
    }

    /// <summary>
    /// Plain text with collapsed spaces. Headings show the message as rendered Markdown, so list numbers,
    /// bullets, heading and quote marks, emphasis and backticks are dropped from both sides.
    /// </summary>
    public static string Normalize(string text)
    {
        text = Regex.Replace(text, @"(?m)^\s*(\d+[.)]|[-*+]|#{1,6}|>)\s+", "");
        text = Regex.Replace(text, @"[*_~`]", "");
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>The loaded prompt headings, in conversation order.</summary>
    public static List<AutomationElement> PromptHeadings(AutomationElement window) =>
        window.FindAll(TreeScope.Descendants, HeadingCondition).Cast<AutomationElement>()
            .Where(h => h.Current.Name.StartsWith(HeadingPrefix, StringComparison.Ordinal))
            .ToList();

    /// <summary>The start of the prompt a heading stands for.</summary>
    public static string PromptStart(AutomationElement heading) => heading.Current.Name[HeadingPrefix.Length..];

    public static void ScrollTo(AutomationElement heading) =>
        ((ScrollItemPattern)heading.GetCurrentPattern(ScrollItemPattern.Pattern)).ScrollIntoView();

    /// <summary>Presses one of the load buttons; false when it isn't there (nothing more to load that way).</summary>
    public static bool Press(AutomationElement window, string name)
    {
        var button = window.FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button),
            new PropertyCondition(AutomationElement.NameProperty, name)));
        if (button == null || !button.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke)) return false;
        ((InvokePattern)invoke).Invoke();
        return true;
    }

    /// <summary>The area the conversation scrolls in: the largest vertically scrollable element.</summary>
    public static System.Windows.Rect? ScrollArea(AutomationElement window) =>
        window.FindAll(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsScrollPatternAvailableProperty, true))
            .Cast<AutomationElement>()
            .Where(e => ((ScrollPattern)e.GetCurrentPattern(ScrollPattern.Pattern)).Current.VerticallyScrollable)
            .Select(e => e.Current.BoundingRectangle)
            .Where(r => !r.IsEmpty)
            .OrderByDescending(r => r.Width * r.Height)
            .Cast<System.Windows.Rect?>()
            .FirstOrDefault();
}
