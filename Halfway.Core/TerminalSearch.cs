namespace Halfway.Core;

public static class TerminalSearch
{
    public static IReadOnlyList<int> FindMatches(string text, string query)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(query)) return [];
        var matches = new List<int>();
        for (var start = 0; start <= text.Length - query.Length;)
        {
            var index = text.IndexOf(query, start, StringComparison.OrdinalIgnoreCase);
            if (index < 0) break;
            matches.Add(index);
            start = index + query.Length;
        }
        return matches;
    }

    public static int Move(int matchCount, int currentIndex, int offset)
    {
        if (matchCount == 0) return -1;
        if (currentIndex < 0 || currentIndex >= matchCount) return offset < 0 ? matchCount - 1 : 0;
        return (currentIndex + offset % matchCount + matchCount) % matchCount;
    }
}
