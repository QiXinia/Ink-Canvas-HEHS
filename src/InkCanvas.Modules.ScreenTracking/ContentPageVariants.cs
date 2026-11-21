namespace InkCanvas.Core.ScreenTracking;

/// <summary>Conservative text fingerprints distinguish large in-place page changes from small counters.</summary>
public sealed class ContentPageVariants
{
    private readonly Dictionary<string, List<HashSet<ulong>>> _variants = new(StringComparer.Ordinal);
    private string _pendingKey;
    private int _pendingVariant = -1, _confirmations;
    private HashSet<ulong> _pendingSignature;
    private readonly Dictionary<string, int> _current = new(StringComparer.Ordinal);

    public string Resolve(string document, string text, bool viewportMoving)
    {
        if (!_current.TryGetValue(document, out int current)) _current[document] = current = 0;
        if (viewportMoving || string.IsNullOrWhiteSpace(text) || text.Length < 80) return document + ":view" + current;
        var signature = Fingerprint(text);
        if (!_variants.TryGetValue(document, out var variants)) _variants[document] = variants = new List<HashSet<ulong>> { signature };
        int matching = variants.FindIndex(known => Similarity(known, signature) >= .6);
        if (matching == current) { _pendingKey = null; _confirmations = 0; return document + ":view" + current; }
        int proposed = matching < 0 ? variants.Count : matching;
        if (_pendingKey == document && _pendingVariant == proposed && Similarity(_pendingSignature, signature) >= .6) _confirmations++;
        else { _pendingKey = document; _pendingVariant = proposed; _confirmations = 1; _pendingSignature = signature; }
        if (_confirmations >= 2)
        {
            if (matching < 0) variants.Add(signature);
            _current[document] = current = proposed;
            _pendingKey = null; _confirmations = 0;
        }
        return document + ":view" + current;
    }

    private static HashSet<ulong> Fingerprint(string text)
    {
        var compact = text.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).Take(4096).ToArray();
        var smallest = new SortedSet<ulong>();
        for (int index = 0; index + 5 <= compact.Length; index++)
        {
            ulong hash = 14695981039346656037;
            for (int offset = 0; offset < 5; offset++) hash = (hash ^ compact[index + offset]) * 1099511628211;
            smallest.Add(hash);
            if (smallest.Count > 96) smallest.Remove(smallest.Max);
        }
        return smallest.ToHashSet();
    }

    private static double Similarity(HashSet<ulong> a, HashSet<ulong> b)
    {
        if (a.Count == 0 || b.Count == 0) return 1;
        return a.Count(value => b.Contains(value)) / (double)Math.Max(a.Count, b.Count);
    }
}
