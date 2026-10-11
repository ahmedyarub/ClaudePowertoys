using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudePowertoys;

sealed record SessionInfo(string SessionId, string CliSessionId, string Title, string Folder, long LastFocusedAt)
{
    /// <summary>Opens the session in Claude Desktop, the same link the app hands out for its sessions.</summary>
    public string Link => $"claude://claude.ai/epitaxy/{SessionId}";
}

static class ClaudeLinks
{
    /// <summary>The app's own "Sessions Waiting for You" action.</summary>
    public const string NeedsInput = "claude://code/needs-input?source=desktop_action";

    /// <summary>A new Code session, in <paramref name="folder"/> when given (the app's jump list uses the same link).</summary>
    public static string NewCodeSession(string? folder) =>
        "claude://code/new?source=desktop_action" + (folder == null ? "" : "&folder=" + Uri.EscapeDataString(folder));

    /// <summary>Opens a claude:// link off the calling thread: ShellExecute can be slow, which a keyboard hook can't afford.</summary>
    public static void Open(string link) => ThreadPool.QueueUserWorkItem(_ =>
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(link) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Write($"Opening {link} failed: {ex.Message}"); }
    });
}

/// <summary>
/// Claude Desktop's recently active Code sessions, read from the app's own files in
/// %APPDATA%\Claude\claude-code-sessions (one local_*.json per session).
/// </summary>
static class RecentSessions
{
    static readonly string SessionsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code-sessions");

    // The fields used are all near the top of the file, ahead of the large arrays.
    const int HeadBytes = 16 * 1024;

    sealed record FileMeta(DateTime WriteTime, string SessionId, string CliSessionId, string Title, string Cwd,
        bool IsArchived, long LastActivityAt, long LastFocusedAt);

    static readonly Dictionary<string, FileMeta> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Unarchived sessions with activity within <paramref name="window"/>, most recently focused first.
    /// The most recently focused session (the one on screen) always comes first, recent or not.
    /// </summary>
    public static List<SessionInfo> Scan(TimeSpan window)
    {
        long since = DateTimeOffset.UtcNow.Add(-window).ToUnixTimeMilliseconds();
        var sessions = ReadSessionFiles()
            .Where(m => !m.IsArchived)
            .OrderByDescending(m => m.LastFocusedAt)
            .ToList();

        return sessions
            .Where((m, i) => i == 0 || m.LastActivityAt >= since)
            .Select(m => new SessionInfo(m.SessionId, m.CliSessionId, m.Title != "" ? m.Title : "Untitled session", FolderName(m.Cwd), m.LastFocusedAt))
            .ToList();
    }

    /// <summary>
    /// Folders of unarchived sessions, most recently active first, the way the app's own jump list
    /// picks them for "New Code Session in". Scratch workspaces ("No folder" sessions) are left out.
    /// </summary>
    public static List<string> RecentFolders(int max) =>
        ReadSessionFiles()
            .Where(m => !m.IsArchived && m.Cwd != "" && !m.Cwd.Contains(@"\Claude\scratch-workspaces\", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.LastActivityAt)
            .Select(m => m.Cwd.TrimEnd('\\', '/'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(Directory.Exists)
            .Take(max)
            .ToList();

    /// <summary>Every session the app has, archived ones included, most recently active first.</summary>
    public static List<(SessionInfo Session, bool IsArchived, long LastActivityAt)> All() =>
        ReadSessionFiles()
            .Where(m => m.CliSessionId != "")
            .OrderByDescending(m => m.LastActivityAt)
            .Select(m => (new SessionInfo(m.SessionId, m.CliSessionId, m.Title != "" ? m.Title : "Untitled session", FolderName(m.Cwd), m.LastFocusedAt),
                m.IsArchived, m.LastActivityAt))
            .ToList();

    /// <summary>The Claude Code session id (its transcript's file name) of the most recently focused session: the one on screen.</summary>
    public static string? OnScreenCliSessionId() => OnScreen()?.CliSessionId;

    /// <summary>The Claude Code session id, title and app session id of the session on screen.</summary>
    public static (string CliSessionId, string Title, string SessionId)? OnScreen()
    {
        var m = ReadSessionFiles().Where(m => !m.IsArchived && m.CliSessionId != "").MaxBy(m => m.LastFocusedAt);
        return m == null ? null : (m.CliSessionId, m.Title != "" ? m.Title : "Untitled session", m.SessionId);
    }

    static string FolderName(string cwd)
    {
        if (cwd == "") return "";
        if (cwd.Contains(@"\Claude\scratch-workspaces\", StringComparison.OrdinalIgnoreCase)) return "No folder";
        return Path.GetFileName(cwd.TrimEnd('\\', '/'));
    }

    static readonly object Gate = new();

    /// <summary>The switcher, the status monitor and the outline button all read these, from different threads.</summary>
    static List<FileMeta> ReadSessionFiles()
    {
        lock (Gate) return ReadSessionFilesLocked();
    }

    static List<FileMeta> ReadSessionFilesLocked()
    {
        var result = new List<FileMeta>();
        if (!Directory.Exists(SessionsRoot)) return result;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in Directory.EnumerateFiles(SessionsRoot, "local_*.json", SearchOption.AllDirectories))
        {
            seen.Add(file);
            DateTime writeTime;
            try { writeTime = File.GetLastWriteTimeUtc(file); }
            catch { continue; }
            if (!Cache.TryGetValue(file, out var meta) || meta.WriteTime != writeTime)
            {
                meta = Parse(file, writeTime);
                if (meta == null) continue;
                Cache[file] = meta;
            }
            result.Add(meta);
        }
        foreach (var stale in Cache.Keys.Where(k => !seen.Contains(k)).ToList()) Cache.Remove(stale);
        return result;
    }

    static FileMeta? Parse(string file, DateTime writeTime)
    {
        string head;
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new byte[HeadBytes];
            int read = fs.Read(buffer, 0, buffer.Length);
            head = Encoding.UTF8.GetString(buffer, 0, read);
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        return new FileMeta(
            writeTime,
            StringField(head, "sessionId") ?? Path.GetFileNameWithoutExtension(file),
            StringField(head, "cliSessionId") ?? "",
            StringField(head, "title") ?? "",
            StringField(head, "cwd") ?? "",
            Regex.Match(head, "\"isArchived\"\\s*:\\s*true").Success,
            NumberField(head, "lastActivityAt"),
            NumberField(head, "lastFocusedAt"));
    }

    static string? StringField(string json, string name)
    {
        var m = Regex.Match(json, $"\"{name}\"\\s*:\\s*(\"(?:[^\"\\\\]|\\\\.)*\")");
        return m.Success ? JsonSerializer.Deserialize<string>(m.Groups[1].Value) : null;
    }

    static long NumberField(string json, string name)
    {
        var m = Regex.Match(json, $"\"{name}\"\\s*:\\s*(\\d+)");
        return m.Success ? long.Parse(m.Groups[1].Value) : 0;
    }
}
