using System.IO;

namespace ClaudePowertoys;

sealed record SearchHit(SessionInfo Session, bool IsArchived, long LastActivityAt, bool FromUser, string Text, int PromptIndex);

/// <summary>
/// Every session's prompts and Claude's replies (not tool calls, results or thinking), for searching.
/// Each transcript is re-read only when it changes.
/// </summary>
static class SearchIndex
{
    const int MaxHits = 200;

    static readonly Dictionary<string, (DateTime WriteTime, List<(bool FromUser, string Text, int PromptIndex)> Parts)> Cache =
        new(StringComparer.OrdinalIgnoreCase);
    static readonly object Gate = new();

    /// <summary>Parts holding every word of the query (any case), newest session first, latest part first.</summary>
    public static List<SearchHit> Search(string query)
    {
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return [];

        var hits = new List<SearchHit>();
        lock (Gate)
        {
            foreach (var (session, archived, activity) in RecentSessions.All())
            {
                var parts = PartsOf(session.CliSessionId);
                for (int i = parts.Count - 1; i >= 0 && hits.Count < MaxHits; i--)
                {
                    var p = parts[i];
                    if (terms.All(t => p.Text.Contains(t, StringComparison.OrdinalIgnoreCase)))
                        hits.Add(new SearchHit(session, archived, activity, p.FromUser, p.Text, p.PromptIndex));
                }
                if (hits.Count >= MaxHits) break;
            }
        }
        return hits;
    }

    /// <summary>Reads every transcript once, so the first search is quick.</summary>
    public static void Warm()
    {
        lock (Gate)
            foreach (var (session, _, _) in RecentSessions.All()) PartsOf(session.CliSessionId);
    }

    static List<(bool FromUser, string Text, int PromptIndex)> PartsOf(string cliSessionId)
    {
        var path = ConversationPrompts.FindTranscript(cliSessionId);
        if (path == null) return [];
        var writeTime = File.GetLastWriteTimeUtc(path);
        if (Cache.TryGetValue(path, out var entry) && entry.WriteTime == writeTime) return entry.Parts;
        try { Cache[path] = entry = (writeTime, ConversationExport.Parts(path)); }
        catch (IOException) { return []; }
        return entry.Parts;
    }

    /// <summary>About <paramref name="width"/> characters around the first term, on one line.</summary>
    public static string Snippet(string text, string query, int width)
    {
        var flat = string.Join(" ", text.Split((char[])['\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var first = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        int at = first == "" ? 0 : Math.Max(0, flat.IndexOf(first, StringComparison.OrdinalIgnoreCase));
        int start = Math.Max(0, at - width / 3);
        int end = Math.Min(flat.Length, start + width);
        return (start > 0 ? "…" : "") + flat[start..end] + (end < flat.Length ? "…" : "");
    }
}
