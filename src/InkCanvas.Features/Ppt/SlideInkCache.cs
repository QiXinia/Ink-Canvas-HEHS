using InkCanvas.Core.Ink;

namespace InkCanvas.Features.Ppt;

/// <summary>
/// Small thread-safe LRU cache for per-slide ink. Callers may read it from the background
/// prefetch worker while the UI thread writes to it, so every entry is a detached deep copy
/// and all access is serialised.
/// </summary>
public sealed class SlideInkCache
{
    private sealed record Entry(int SlideId, IReadOnlyList<InkStroke> Strokes);

    private readonly int _capacity;
    private readonly object _sync = new();
    private readonly Dictionary<int, LinkedListNode<Entry>> _entries = new();
    private readonly LinkedList<Entry> _lru = new();
    private long _hits;
    private long _misses;

    public SlideInkCache(int capacity = 8)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    public int Capacity => _capacity;

    public int Count
    {
        get { lock (_sync) return _entries.Count; }
    }

    public long Hits
    {
        get { lock (_sync) return _hits; }
    }

    public long Misses
    {
        get { lock (_sync) return _misses; }
    }

    /// <summary>True when the slide is cached. Does not affect the hit/miss counters.</summary>
    public bool Contains(int slideId)
    {
        lock (_sync) return _entries.ContainsKey(slideId);
    }

    /// <summary>
    /// Returns a detached copy of the cached strokes, or <c>null</c> on a miss. Every hit
    /// refreshes the entry's LRU position.
    /// </summary>
    public IReadOnlyList<InkStroke> Get(int slideId)
    {
        lock (_sync)
        {
            if (!_entries.TryGetValue(slideId, out LinkedListNode<Entry> node))
            {
                _misses++;
                return null;
            }

            _hits++;
            _lru.Remove(node);
            _lru.AddFirst(node);
            return Clone(node.Value.Strokes);
        }
    }

    /// <summary>Stores a deep copy, replacing any previous entry for the slide.</summary>
    public void Set(int slideId, IReadOnlyList<InkStroke> strokes)
    {
        IReadOnlyList<InkStroke> copy = Clone(strokes);
        lock (_sync)
        {
            if (_entries.TryGetValue(slideId, out LinkedListNode<Entry> existing))
            {
                existing.Value = new Entry(slideId, copy);
                _lru.Remove(existing);
                _lru.AddFirst(existing);
                return;
            }

            var node = new LinkedListNode<Entry>(new Entry(slideId, copy));
            _lru.AddFirst(node);
            _entries[slideId] = node;

            while (_entries.Count > _capacity && _lru.Last is { } last)
            {
                _lru.RemoveLast();
                _entries.Remove(last.Value.SlideId);
            }
        }
    }

    public void Clear()
    {
        lock (_sync)
        {
            _entries.Clear();
            _lru.Clear();
        }
    }

    private static IReadOnlyList<InkStroke> Clone(IEnumerable<InkStroke> strokes) =>
        strokes?.Where(s => s is not null).Select(s => s.Clone()).ToList() ?? new List<InkStroke>();
}