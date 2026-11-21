namespace InkCanvas.Core.Settings;

public sealed record SettingsSearchEntry(string Category, string Title, string Keywords)
{
    public string Key => Category + "\n" + Title;
    public override string ToString() => Title + " · " + Category;
}

public static class SettingsSearch
{
    public static IReadOnlyList<SettingsSearchEntry> Find(IEnumerable<SettingsSearchEntry> entries, string query)
    {
        query = query?.Trim();
        if (string.IsNullOrEmpty(query)) return Array.Empty<SettingsSearchEntry>();
        var words = query.Split((char[])null, StringSplitOptions.RemoveEmptyEntries);
        return entries.Where(entry => words.All(word => Contains(entry.Title, word)
                || Contains(entry.Category, word) || Contains(entry.Keywords, word)))
            .OrderBy(entry => Rank(entry, query, words)).ToArray();
    }

    private static int Rank(SettingsSearchEntry entry, string query, string[] words)
    {
        if (string.Equals(entry.Title, query, StringComparison.OrdinalIgnoreCase)) return 0;
        if (entry.Title.StartsWith(query, StringComparison.OrdinalIgnoreCase)) return 1;
        if (Contains(entry.Title, query)) return 2;
        if (words.All(word => Contains(entry.Title, word))) return 3;
        return words.All(word => Contains(entry.Title, word) || Contains(entry.Keywords, word)) ? 4 : 5;
    }

    private static bool Contains(string text, string query) => text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
}
