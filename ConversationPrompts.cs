using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ClaudePowertoys;

/// <summary>
/// The prompts the person typed in a Claude Code session, in order, from its transcript
/// (~/.claude/projects/&lt;project&gt;/&lt;cliSessionId&gt;.jsonl). Typed prompts are "user" entries with
/// text content; prompts sent while Claude was working are "queued_command" attachments.
/// Tool results and injected text (isMeta, or wrapped in tags such as &lt;system-reminder&gt;) are not prompts.
/// </summary>
static partial class ConversationPrompts
{
    static readonly string ProjectsRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    public static string? FindTranscript(string cliSessionId)
    {
        if (!Directory.Exists(ProjectsRoot)) return null;
        foreach (var dir in Directory.EnumerateDirectories(ProjectsRoot))
        {
            var path = Path.Combine(dir, cliSessionId + ".jsonl");
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static List<string> Read(string transcriptPath)
    {
        var prompts = new List<string>();
        using var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
        {
            // Cheap filter before parsing: most lines are assistant messages and tool traffic.
            if (!line.Contains("\"type\":\"user\"") && !line.Contains("\"queued_command\"")) continue;
            var text = PromptOf(line);
            if (text != null) prompts.Add(text);
        }
        return prompts;
    }

    /// <summary>The prompt a transcript line records, or null when it records something else.</summary>
    public static string? PromptOf(string line)
    {
        try { return PromptText(line); }
        catch (JsonException) { return null; }
        catch (InvalidOperationException) { return null; }
        catch (KeyNotFoundException) { return null; }
    }

    static string? PromptText(string line)
    {
        using var doc = JsonDocument.Parse(line);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;

        if (type == "attachment")
        {
            if (!root.TryGetProperty("attachment", out var a) || a.GetProperty("type").GetString() != "queued_command") return null;
            if (a.TryGetProperty("origin", out var origin) && origin.TryGetProperty("kind", out var kind) && kind.GetString() != "human") return null;
            return Clean(a.TryGetProperty("prompt", out var p) ? p.GetString() : null);
        }

        if (type != "user" || (root.TryGetProperty("isMeta", out var meta) && meta.ValueKind == JsonValueKind.True)) return null;
        if (!root.TryGetProperty("message", out var message) || !message.TryGetProperty("content", out var content)) return null;
        if (content.ValueKind == JsonValueKind.String) return Clean(content.GetString());
        if (content.ValueKind != JsonValueKind.Array) return null;

        var parts = new List<string>();
        foreach (var block in content.EnumerateArray())
        {
            var blockType = block.TryGetProperty("type", out var bt) ? bt.GetString() : null;
            if (blockType == "tool_result") return null;
            if (blockType == "text" && block.TryGetProperty("text", out var bText)) parts.Add(bText.GetString() ?? "");
        }
        return Clean(string.Join("\n", parts));
    }

    /// <summary>Drops leading injected blocks such as &lt;system-reminder&gt;…&lt;/system-reminder&gt;; null when nothing typed is left.</summary>
    static string? Clean(string? text)
    {
        if (text == null) return null;
        text = LeadingTagBlock().Replace(text, "").Trim();
        // "[Request interrupted by user…]" is written by Claude Code when a turn or tool call is stopped.
        return text == "" || text.StartsWith('<') || text.StartsWith("[Request interrupted by user", StringComparison.Ordinal) ? null : text;
    }

    [GeneratedRegex(@"^(\s*<(?<tag>[a-z][\w-]*)>.*?</\k<tag>>)+", RegexOptions.Singleline)]
    private static partial Regex LeadingTagBlock();
}
