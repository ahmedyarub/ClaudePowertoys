using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudePowertoys;

sealed record SessionInfo(string SessionId, string Title, string Folder, long LastFocusedAt)
{
    /// <summary>Opens the session in Claude Desktop, the same link the app hands out for its sessions.</summary>
    public string Link => $"claude://claude.ai/epitaxy/{SessionId}";
}

/// <summary>
/// The Claude Code sessions Claude Desktop has running: each has a claude.exe process under
/// %APPDATA%\Claude\claude-code. Session metadata comes from the app's own files in
/// %APPDATA%\Claude\claude-code-sessions (one local_*.json per session).
///
/// A reopened session's process carries --resume=&lt;cliSessionId&gt;. A session started fresh in this
/// app run has no id on its command line: the app keeps a spare process warmed up and hands it to the
/// next new session, so such a process belongs to the first session created after it started
/// (observed 13-33 s apart). A spare not yet handed out matches no session.
/// </summary>
static partial class ActiveSessions
{
    static readonly string SessionsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude-code-sessions");

    // The fields used are all near the top of the file, ahead of the large arrays.
    const int HeadBytes = 16 * 1024;

    sealed record FileMeta(DateTime WriteTime, string SessionId, string CliSessionId, string Title, string Cwd,
        bool IsArchived, long CreatedAt, long LastFocusedAt);

    static readonly Dictionary<string, FileMeta> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Running sessions, most recently focused first.</summary>
    public static List<SessionInfo> Scan()
    {
        var metas = ReadSessionFiles();
        var byCliId = metas.Where(m => m.CliSessionId != "").ToDictionary(m => m.CliSessionId, StringComparer.OrdinalIgnoreCase);
        var running = new List<FileMeta>();
        var fresh = new List<DateTime>();

        foreach (var p in Process.GetProcessesByName("claude"))
        {
            using (p)
            {
                string? path;
                try { path = p.MainModule?.FileName; }
                catch { continue; }
                if (path == null || !path.Contains(@"\Claude\claude-code\", StringComparison.OrdinalIgnoreCase)) continue;

                var commandLine = ProcessCommandLine.Read(p.Id);
                if (commandLine == null) continue;
                var resume = ResumeArg().Match(commandLine);
                if (resume.Success)
                {
                    if (byCliId.TryGetValue(resume.Groups[1].Value, out var meta)) running.Add(meta);
                }
                else
                {
                    try { fresh.Add(p.StartTime.ToUniversalTime()); }
                    catch { }
                }
            }
        }

        var taken = new HashSet<FileMeta>(running);
        foreach (var start in fresh.Order())
        {
            long startMs = new DateTimeOffset(start).ToUnixTimeMilliseconds();
            var match = metas
                .Where(m => !taken.Contains(m) && m.CreatedAt >= startMs)
                .MinBy(m => m.CreatedAt);
            if (match == null) continue;
            running.Add(match);
            taken.Add(match);
        }

        return running
            .Where(m => !m.IsArchived)
            .OrderByDescending(m => m.LastFocusedAt)
            .Select(m => new SessionInfo(m.SessionId, DisplayTitle(m), FolderName(m.Cwd), m.LastFocusedAt))
            .ToList();
    }

    static string DisplayTitle(FileMeta m) => m.Title != "" ? m.Title : "Untitled session";

    static string FolderName(string cwd)
    {
        if (cwd == "") return "";
        if (cwd.Contains(@"\Claude\scratch-workspaces\", StringComparison.OrdinalIgnoreCase)) return "No folder";
        return Path.GetFileName(cwd.TrimEnd('\\', '/'));
    }

    static List<FileMeta> ReadSessionFiles()
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
            NumberField(head, "createdAt"),
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

    [GeneratedRegex(@"--resume[ =]""?([0-9a-fA-F-]{36})")]
    private static partial Regex ResumeArg();
}

static class ProcessCommandLine
{
    const int ProcessCommandLineInformation = 60;
    const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    public static string? Read(int pid)
    {
        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            NtQueryInformationProcess(handle, ProcessCommandLineInformation, IntPtr.Zero, 0, out int needed);
            if (needed <= 0) return null;
            var buffer = Marshal.AllocHGlobal(needed);
            try
            {
                if (NtQueryInformationProcess(handle, ProcessCommandLineInformation, buffer, needed, out _) != 0) return null;
                // UNICODE_STRING { ushort Length; ushort MaximumLength; IntPtr Buffer; }
                int length = Marshal.ReadInt16(buffer) & 0xFFFF;
                var text = Marshal.ReadIntPtr(buffer, IntPtr.Size);
                return Marshal.PtrToStringUni(text, length / 2);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(handle); }
    }

    [DllImport("ntdll.dll")] static extern int NtQueryInformationProcess(IntPtr process, int infoClass, IntPtr info, int length, out int returnLength);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, int pid);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
}
