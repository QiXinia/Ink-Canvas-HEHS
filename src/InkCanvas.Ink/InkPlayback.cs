using InkCanvas.Core.Ink;
using InkCanvas.Core.Replay;

namespace InkCanvas.Ink;

/// <summary>Display-only playback over the existing page; source strokes and history stay intact.</summary>
public sealed class InkPlayback
{
    private readonly HashSet<InkStroke> _targets;
    private readonly InkReplaySession _session;
    private readonly List<InkStroke> _visible = new();
    public IReadOnlyList<InkStroke> VisibleStrokes => _visible;
    public TimeSpan Duration => _session.Duration;
    public bool Includes(InkStroke stroke) => _targets.Contains(stroke);
    public InkPlayback(IEnumerable<InkStroke> strokes)
    {
        var targets = strokes.ToList();
        _targets = targets.ToHashSet();
        _session = new InkReplaySession(targets.Select(s => s.Clone()));
        Seek(0);
    }
    public void Seek(float elapsedMs) => _session.WriteVisibleAt(elapsedMs, _visible);
}
