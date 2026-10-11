using System.IO;
using System.Text;
using System.Text.Json;

namespace ClaudePowertoys;

/// <summary>
/// The readable part of a Claude Code transcript: the prompts typed (see ConversationPrompts) and the
/// text Claude wrote, in order. Tool calls, tool results and thinking are left out.
/// </summary>
static class ConversationExport
{
    sealed record Part(bool FromUser, string Text, string? MessageId, bool EndsTurn);

    /// <summary>
    /// The conversation's prompts and Claude's text in order, each with the index of the prompt it
    /// belongs to (its own index for a prompt; the prompt before it for Claude's text, -1 before any).
    /// Prompt indexes match ConversationPrompts.Read.
    /// </summary>
    public static List<(bool FromUser, string Text, int PromptIndex)> Parts(string transcriptPath)
    {
        var result = new List<(bool, string, int)>();
        int prompt = -1;
        foreach (var p in Read(transcriptPath))
        {
            if (p.FromUser) prompt++;
            result.Add((p.FromUser, p.Text, prompt));
        }
        return result;
    }

    /// <summary>The text of Claude's last finished reply: every text block of its last end_turn message.</summary>
    public static string? LastAnswer(string transcriptPath)
    {
        var parts = Read(transcriptPath);
        var last = parts.LastOrDefault(p => !p.FromUser && p.EndsTurn);
        if (last?.MessageId == null) return null;
        return string.Join("\n\n", parts.Where(p => !p.FromUser && p.MessageId == last.MessageId).Select(p => p.Text));
    }

    public static string ToMarkdown(string transcriptPath, string title)
    {
        var sb = new StringBuilder($"# {title}\n");
        bool? lastFromUser = null;
        foreach (var part in Read(transcriptPath))
        {
            if (part.FromUser != lastFromUser) sb.Append(part.FromUser ? "\n## You\n\n" : "\n## Claude\n\n");
            else sb.Append('\n');
            sb.Append(part.Text.Trim()).Append('\n');
            lastFromUser = part.FromUser;
        }
        return sb.ToString();
    }

    static List<Part> Read(string transcriptPath)
    {
        var parts = new List<Part>();
        using var fs = new FileStream(transcriptPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(fs);
        while (reader.ReadLine() is { } line)
        {
            try
            {
                if (line.Contains("\"type\":\"assistant\"")) AddAssistantText(line, parts);
                else if (line.Contains("\"type\":\"user\"") || line.Contains("\"queued_command\""))
                {
                    var prompt = ConversationPrompts.PromptOf(line);
                    if (prompt != null) parts.Add(new Part(true, prompt, null, false));
                }
            }
            catch (JsonException) { }
        }
        return parts;
    }

    static void AddAssistantText(string line, List<Part> parts)
    {
        using var doc = JsonDocument.Parse(line);
        if (!doc.RootElement.TryGetProperty("message", out var message)) return;
        if (!message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) return;
        var id = message.TryGetProperty("id", out var i) ? i.GetString() : null;
        bool endsTurn = message.TryGetProperty("stop_reason", out var s) && s.ValueKind == JsonValueKind.String && s.GetString() == "end_turn";
        foreach (var block in content.EnumerateArray())
            if (block.TryGetProperty("type", out var t) && t.GetString() == "text"
                && block.TryGetProperty("text", out var text) && !string.IsNullOrWhiteSpace(text.GetString()))
                parts.Add(new Part(false, text.GetString()!, id, endsTurn));
    }
}
