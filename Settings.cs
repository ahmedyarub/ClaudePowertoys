using System.IO;
using System.Text.Json;

namespace ClaudePowertoys;

sealed class Settings
{
    public bool AutoHideSidebar { get; set; }
    public bool RemoveResizeHandle { get; set; } = true;
    public bool SessionSwitcher { get; set; } = true;
    public bool CtrlEnterSendsNow { get; set; } = true;
    public bool OutlineButton { get; set; } = true;
    public bool PromptJump { get; set; } = true;
    public bool Snippets { get; set; } = true;
    public bool PromptHistory { get; set; } = true;
    public bool NeedsInputHotkey { get; set; } = true;
    /// <summary>The combination for "Sessions waiting for you". Win+Shift+C is taken on this machine.</summary>
    public string NeedsInputHotkeyKeys { get; set; } = "Win+Alt+C";
    public bool NewSessionHotkey { get; set; } = true;
    public string NewSessionHotkeyKeys { get; set; } = "Win+Alt+N";
    /// <summary>How far back a session's last activity may be for the Ctrl+Tab list.</summary>
    public double RecentSessionHours { get; set; } = 8;

    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudePowertoys");
    static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static Settings Load()
    {
        if (!File.Exists(FilePath)) return new Settings();
        return JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}

static class Log
{
    static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudePowertoys", "log.txt");
    static readonly object Gate = new();

    public static void Write(string message)
    {
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.AppendAllText(FilePath, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
    }
}
