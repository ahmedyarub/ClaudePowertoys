using System.IO;
using System.Runtime.InteropServices;

namespace ClaudePowertoys;

/// <summary>
/// A menu at the mouse of the folders recent sessions worked in; picking one starts a new Code
/// session there through the app's claude://code/new?folder= link.
/// </summary>
static class NewSessionMenu
{
    const int MaxFolders = 12;

    /// <param name="owner">A window of this process: a hotkey press lets it take the foreground, which a menu needs to get the keyboard.</param>
    public static void Show(Control owner)
    {
        var menu = new ContextMenuStrip { ShowImageMargin = false, Renderer = DarkMenu.Renderer };
        menu.Closed += (_, _) => menu.BeginInvoke(menu.Dispose);
        menu.Items.Add(new ToolStripMenuItem("New Code session in…") { Enabled = false });

        var folders = RecentSessions.RecentFolders(MaxFolders);
        for (int i = 0; i < folders.Count; i++)
        {
            var folder = folders[i];
            var label = (i < 9 ? $"&{i + 1}  " : "    ") + Path.GetFileName(folder);
            menu.Items.Add(new ToolStripMenuItem(label, null, (_, _) => Start(folder)) { ToolTipText = folder });
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(new ToolStripMenuItem("&0  No folder", null, (_, _) => Start(null)));

        SetForegroundWindow(owner.Handle);
        menu.Show(Cursor.Position);
    }

    static void Start(string? folder)
    {
        Log.Write($"New Code session in {folder ?? "no folder"}");
        ClaudeLinks.Open(ClaudeLinks.NewCodeSession(folder));
    }

    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hwnd);
}
