using System.IO;
using System.Text;
using System.Text.Json;

namespace ClaudePowertoys;

enum SessionState
{
    Idle,
    Working,
    /// <summary>Claude asked a question or wants a plan approved, and nobody has answered.</summary>
    NeedsYou,
    /// <summary>The turn finished after the person last looked at the session.</summary>
    NewReply,
}

/// <param name="TurnStartedAt">Unix ms of the prompt that started the current turn; meaningful while working or needing you.</param>
sealed record SessionStatusInfo(SessionState State, long ContextTokens, long TurnStartedAt)
{
    /// <summary>"12 min", "2 h 5 min": how long the current turn has been going.</summary>
    public string TurnAge()
    {
        if (TurnStartedAt == 0) return "";
        var age = DateTimeOffset.UtcNow - DateTimeOffset.FromUnixTimeMilliseconds(TurnStartedAt);
        return age.TotalMinutes < 1 ? "<1 min" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min" : $"{(int)age.TotalHours} h {age.Minutes} min";
    }
}

/// <summary>
/// A session's state, read from its Claude Code transcript as it grows (each file is only read from
/// where the last read stopped). The last thing that happened decides: Claude's message ending its
/// turn (stop_reason "end_turn"), Claude waiting on tool calls (stop_reason "tool_use" with calls
/// not yet answered), or a prompt / tool result Claude is now working on. A permission prompt is not
/// in the transcript, so a session waiting on one reads as working.
/// </summary>
static class SessionStatus
{
    static readonly HashSet<string> AskingTools = ["AskUserQuestion", "ExitPlanMode"];

    sealed class Tail
    {
        public long Offset;
        public string Last = "";          // "end", "tools", "working", "interrupted"
        public long EndedAt;              // unix ms of the last end_turn
        public long StartedAt;            // unix ms of the prompt that started the current turn
        public readonly Dictionary<string, string> Pending = [];
        public long ContextTokens;
    }

    static readonly Dictionary<string, Tail> Tails = new(StringComparer.OrdinalIgnoreCase);
    static readonly Dictionary<string, string?> Paths = new(StringComparer.OrdinalIgnoreCase);
    static readonly object Gate = new();

    /// <param name="seenAt">Unix ms when the person last had the session in front of them; a turn ending later is a new reply.</param>
    public static SessionStatusInfo? Read(string cliSessionId, long seenAt)
    {
        lock (Gate)
        {
            if (!Paths.TryGetValue(cliSessionId, out var path) || path == null || !File.Exists(path))
                Paths[cliSessionId] = path = ConversationPrompts.FindTranscript(cliSessionId);
            if (path == null) return null;

            if (!Tails.TryGetValue(path, out var tail)) Tails[path] = tail = new Tail();
            ReadNew(path, tail);

            var state = tail.Last switch
            {
                "end" => tail.EndedAt > seenAt ? SessionState.NewReply : SessionState.Idle,
                "tools" => tail.Pending.Values.Any(AskingTools.Contains) ? SessionState.NeedsYou : SessionState.Working,
                "working" => SessionState.Working,
                _ => SessionState.Idle,
            };
            return new SessionStatusInfo(state, tail.ContextTokens, tail.StartedAt);
        }
    }

    static void ReadNew(string path, Tail tail)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (fs.Length < tail.Offset) { tail.Offset = 0; tail.Pending.Clear(); tail.Last = ""; }
        if (fs.Length == tail.Offset) return;
        fs.Seek(tail.Offset, SeekOrigin.Begin);

        var bytes = new byte[fs.Length - tail.Offset];
        int read = fs.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        // Only complete lines: the writer may be mid-line.
        int end = Array.LastIndexOf(bytes, (byte)'\n', read - 1);
        if (end < 0) return;
        tail.Offset += end + 1;

        foreach (var line in Encoding.UTF8.GetString(bytes, 0, end).Split('\n'))
        {
            if (!line.Contains("\"type\":\"assistant\"") && !line.Contains("\"type\":\"user\"")) continue;
            try { Apply(line, tail); }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
            catch (KeyNotFoundException) { }
        }
    }

    static void Apply(string line, Tail tail)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = root.GetProperty("type").GetString();
        if (!root.TryGetProperty("message", out var message)) return;

        if (type == "assistant")
        {
            if (message.TryGetProperty("usage", out var usage))
                tail.ContextTokens = Number(usage, "input_tokens") + Number(usage, "cache_read_input_tokens") + Number(usage, "cache_creation_input_tokens");
            var stop = message.TryGetProperty("stop_reason", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            if (message.TryGetProperty("content", out var blocks) && blocks.ValueKind == JsonValueKind.Array)
                foreach (var b in blocks.EnumerateArray())
                    if (b.TryGetProperty("type", out var bt) && bt.GetString() == "tool_use")
                        tail.Pending[b.GetProperty("id").GetString()!] = b.GetProperty("name").GetString() ?? "";
            if (stop == "end_turn")
            {
                tail.Last = "end";
                tail.Pending.Clear();
                tail.EndedAt = Timestamp(root);
            }
            else if (stop == "tool_use") tail.Last = "tools";
            return;
        }

        // A user entry: tool results answer pending calls; anything else typed starts Claude working.
        if (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True) return;
        if (!message.TryGetProperty("content", out var content)) return;
        if (content.ValueKind == JsonValueKind.Array)
        {
            bool results = false;
            foreach (var b in content.EnumerateArray())
                if (b.TryGetProperty("type", out var bt) && bt.GetString() == "tool_result")
                {
                    results = true;
                    tail.Pending.Remove(b.GetProperty("tool_use_id").GetString()!);
                }
            if (results)
            {
                tail.Last = tail.Pending.Count > 0 ? "tools" : "working";
                return;
            }
        }
        var text = content.ValueKind == JsonValueKind.String ? content.GetString() ?? "" : content.ToString();
        bool interrupted = text.Contains("[Request interrupted by user");
        // A prompt after a finished turn starts a new one; a prompt mid-turn joins the running one.
        if (!interrupted && tail.Last is "" or "end" or "interrupted") tail.StartedAt = Timestamp(root);
        tail.Last = interrupted ? "interrupted" : "working";
        tail.Pending.Clear();
    }

    static long Number(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : 0;

    static long Timestamp(JsonElement root) =>
        root.TryGetProperty("timestamp", out var t) && DateTimeOffset.TryParse(t.GetString(), out var at) ? at.ToUnixTimeMilliseconds() : 0;
}
