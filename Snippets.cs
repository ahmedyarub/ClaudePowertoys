using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;
using static ClaudePowertoys.KeyboardHook;

namespace ClaudePowertoys;

sealed record Snippet(string Name, string Text);

/// <summary>
/// Reusable prompts from %APPDATA%\ClaudePowertoys\snippets.json. Ctrl+Shift+Space in Claude opens a
/// picker above the prompt box; the tray menu lists them too. A snippet is typed into the prompt box
/// at the cursor (line breaks as Shift+Enter, so nothing is sent), which leaves the clipboard alone.
/// </summary>
sealed class SnippetInserter : IDisposable
{
    const int VK_SPACE = 0x20;

    public static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudePowertoys", "snippets.json");

    static readonly Snippet[] Examples =
    [
        new("Commit and push", "Commit (signed), push, and list the components that need to be redeployed."),
        new("Review the diff", "Review the current diff for bugs before I commit. Report findings only; don't change anything yet."),
    ];

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    readonly KeyboardHook _hook;
    readonly Control _ui = new();

    public volatile bool Enabled;

    public SnippetInserter()
    {
        _ = _ui.Handle;
        _hook = new KeyboardHook("Snippets", (message, vk) => Enabled && Handle(message, vk));
    }

    public void Dispose()
    {
        _hook.Dispose();
        _ui.Dispose();
    }

    public static List<Snippet> Load()
    {
        if (!File.Exists(FilePath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Examples, Json));
        }
        try { return JsonSerializer.Deserialize<List<Snippet>>(File.ReadAllText(FilePath), Json) ?? []; }
        catch (JsonException ex)
        {
            Log.Write($"snippets.json is not valid JSON: {ex.Message}");
            return [];
        }
    }

    public static void OpenFile()
    {
        Load();
        Process.Start(new ProcessStartInfo(FilePath) { UseShellExecute = true })?.Dispose();
    }

    bool Handle(int message, int vk)
    {
        if (vk != VK_SPACE || !KeyHeld(VK_CONTROL) || !KeyHeld(VK_SHIFT) || KeyHeld(VK_MENU)) return false;
        if (!ClaudeWindow.IsInFront() || FocusTracker.InOtherTextField) return false;
        if (IsDown(message)) _ui.BeginInvoke(ShowPicker);
        return true;
    }

    /// <summary>The picker, just above the prompt box. Runs on the UI thread.</summary>
    void ShowPicker()
    {
        var box = PromptBox();
        var at = box == null ? Cursor.Position : new Point((int)box.Current.BoundingRectangle.Left, (int)box.Current.BoundingRectangle.Top);
        var menu = new ContextMenuStrip { ShowImageMargin = false, Renderer = DarkMenu.Renderer };
        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose);
        AddItems(menu.Items);
        menu.Show(at, ToolStripDropDownDirection.AboveRight);
    }

    /// <summary>The snippets as menu items, for the picker and the tray menu.</summary>
    public static void AddItems(ToolStripItemCollection items)
    {
        var snippets = Load();
        if (snippets.Count == 0) items.Add(new ToolStripMenuItem("No snippets: add some with Edit snippets…") { Enabled = false });
        for (int i = 0; i < snippets.Count; i++)
        {
            var s = snippets[i];
            var label = i < 9 ? $"&{i + 1}  {s.Name}" : $"    {s.Name}";
            items.Add(new ToolStripMenuItem(label, null, (_, _) => Insert(s)) { ToolTipText = s.Text });
        }
    }

    /// <summary>
    /// A menu can take focus from Claude (always from the tray), so Claude is brought forward and its
    /// prompt box focused before typing. Picking from a menu lets this process set the foreground.
    /// </summary>
    static void Insert(Snippet snippet)
    {
        var claude = ClaudeWindow.Find();
        if (claude == IntPtr.Zero) return;
        SetForegroundWindow(claude);
        var box = PromptBox();
        if (box == null)
        {
            Log.Write($"Snippet '{snippet.Name}': no prompt box on screen");
            return;
        }
        box.SetFocus();
        Log.Write($"Snippet '{snippet.Name}' inserted");
        TextTyper.Type(snippet.Text);
    }

    static AutomationElement? PromptBox()
    {
        var claude = ClaudeWindow.Find();
        if (claude == IntPtr.Zero) return null;
        return AutomationElement.FromHandle(claude).FindFirst(TreeScope.Descendants, new AndCondition(
            new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit),
            new PropertyCondition(AutomationElement.NameProperty, "Prompt")));
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
}
