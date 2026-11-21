using InkCanvas.Platform.Input;
using InkCanvas.Platform.Windows;

namespace InkCanvas.Features.Ppt;

/// <summary>How the shell can currently drive the presentation, best first.</summary>
public enum PresentationSourceLevel { Com, Window, Keys, None }

/// <summary>
/// One navigation strategy. Implementations must never throw: a failed navigation simply
/// reports <c>false</c> so <see cref="PresentationSourceSelector"/> can degrade.
/// </summary>
public interface IPresentationSource
{
    PresentationSourceLevel Level { get; }
    bool IsAvailable { get; }
    string Name { get; }
    bool Next();
    bool Previous();
}

/// <summary>
/// Wraps the existing COM adapter through delegates so the Features layer never references
/// the App project or a concrete interop service.
/// </summary>
public sealed class ComPresentationSource : IPresentationSource
{
    private readonly Func<bool> _isAvailable;
    private readonly Func<bool> _next;
    private readonly Func<bool> _previous;

    public ComPresentationSource(Func<bool> isAvailable, Func<bool> next, Func<bool> previous)
    {
        _isAvailable = isAvailable;
        _next = next;
        _previous = previous;
    }

    public PresentationSourceLevel Level => PresentationSourceLevel.Com;
    public string Name => "COM";

    public bool IsAvailable
    {
        get
        {
            try { return _isAvailable?.Invoke() == true; }
            catch { return false; }
        }
    }

    public bool Next()
    {
        try { return _next?.Invoke() == true; }
        catch { return false; }
    }

    public bool Previous()
    {
        try { return _previous?.Invoke() == true; }
        catch { return false; }
    }
}

/// <summary>
/// Degraded path: a full-screen viewer window was detected through the Win32 heuristic, but
/// automation is unavailable. Navigation still has to be delivered as Page Up/Page Down.
/// </summary>
public sealed class WindowPresentationSource : IPresentationSource
{
    private readonly Func<bool> _detect;

    public WindowPresentationSource(Func<bool> detect = null)
    {
        _detect = detect ?? (() => PresentationFallbackDetector.Detect() is not null);
    }

    public PresentationSourceLevel Level => PresentationSourceLevel.Window;
    public string Name => "Window";

    public bool IsAvailable
    {
        get
        {
            try { return _detect(); }
            catch { return false; }
        }
    }

    public bool Next() => KeyboardInjector.PageDown();
    public bool Previous() => KeyboardInjector.PageUp();
}

/// <summary>Last resort: unconditional key injection. Always selectable.</summary>
public sealed class KeyPresentationSource : IPresentationSource
{
    public PresentationSourceLevel Level => PresentationSourceLevel.Keys;
    public string Name => "Keys";
    public bool IsAvailable => true;
    public bool Next() => KeyboardInjector.PageDown();
    public bool Previous() => KeyboardInjector.PageUp();
}

/// <summary>
/// Resolves Com → Window → Keys and remembers an explicit degradation. Once
/// <see cref="ReportFailure"/> lowers the floor, <see cref="Select"/> cannot silently promote
/// the session again until <see cref="Reset"/>; a new slide show starts over at COM.
/// </summary>
public sealed class PresentationSourceSelector
{
    private readonly object _sync = new();
    private PresentationSourceLevel _active = PresentationSourceLevel.Com;
    private PresentationSourceLevel? _floor;
    private string _lastReason;

    public PresentationSourceLevel Active
    {
        get { lock (_sync) return _active; }
    }

    public string LastReason
    {
        get { lock (_sync) return _lastReason; }
    }

    /// <summary>Chooses the best currently available source, never above the failure floor.</summary>
    public void Select(bool comAvailable, bool windowAvailable)
    {
        PresentationSourceLevel best = comAvailable
            ? PresentationSourceLevel.Com
            : windowAvailable ? PresentationSourceLevel.Window : PresentationSourceLevel.Keys;

        lock (_sync)
        {
            if (_floor is { } floor && (int)best < (int)floor) best = floor;
            _active = best;
        }
    }

    /// <summary>
    /// Degrades below <paramref name="level"/> by one step and records why. Unknown levels and
    /// repeated reports are safe; this method never throws.
    /// </summary>
    public void ReportFailure(PresentationSourceLevel level)
    {
        PresentationSourceLevel next;
        string reason;
        switch (level)
        {
            case PresentationSourceLevel.Com:
                next = PresentationSourceLevel.Window;
                reason = "COM 导航失败，降级到窗口检测";
                break;
            case PresentationSourceLevel.Window:
                next = PresentationSourceLevel.Keys;
                reason = "窗口检测不可用，降级到按键注入";
                break;
            case PresentationSourceLevel.Keys:
                next = PresentationSourceLevel.None;
                reason = "按键注入失败，已无可用的翻页源";
                break;
            default:
                return;
        }

        lock (_sync)
        {
            _active = next;
            _floor = next;
            _lastReason = reason;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _active = PresentationSourceLevel.Com;
            _floor = null;
            _lastReason = null;
        }
    }
}