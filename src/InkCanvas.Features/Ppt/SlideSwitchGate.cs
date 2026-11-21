namespace InkCanvas.Features.Ppt;

/// <summary>
/// Invalidates in-flight asynchronous slide work. Callers capture (session, generation) when
/// starting work and drop the result unless <see cref="IsCurrent"/> still holds.
/// </summary>
public sealed class SlideSwitchGate
{
    private readonly object _sync = new();
    private int _session;
    private int _generation;

    public int Session
    {
        get { lock (_sync) return _session; }
    }

    public int Generation
    {
        get { lock (_sync) return _generation; }
    }

    public void BeginSession()
    {
        lock (_sync)
        {
            _session++;
            _generation = 0;
        }
    }

    public int NextGeneration()
    {
        lock (_sync)
        {
            _generation++;
            return _generation;
        }
    }

    public bool IsCurrent(int session, int generation)
    {
        lock (_sync)
        {
            return session == _session && generation == _generation;
        }
    }
}