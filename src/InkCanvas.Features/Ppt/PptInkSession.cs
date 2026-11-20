using InkCanvas.Core.Ink;

namespace InkCanvas.Features.Ppt;

/// <summary>
/// Coordinates per-slide ink and user-facing hooks around <see cref="PptInteropService"/>.
/// Construct/use on the UI dispatcher so callbacks can safely bridge the host ink controller.
/// This service never owns the app window or controller.
/// </summary>
public sealed class PptInkSession : IDisposable
{
    private readonly PptInteropService _interop;
    private readonly IPptInkStore _store;
    private readonly Dictionary<int, List<InkStroke>> _slides = new();
    private readonly Dictionary<int, int> _slideIndexes = new();
    private PptSnapshot _last = PptSnapshot.Detached;
    private PptPresentationInfo _loadedPresentation;
    private string _noticeIdentity;
    private bool _showActive;
    private bool _disposed;
    private int? _lastSlideIndex;
    private int? _lastShowPosition;
    private bool _legacyFilesDetected;
    private DateTime _suppressTransitionScreenshotUntilUtc;

    public PptInkSession(PptInteropService interop, IPptInkStore store = null)
    {
        _interop = interop ?? throw new ArgumentNullException(nameof(interop));
        _store = store;
        _interop.Failed += OnInteropFailed;
    }

    public Func<IReadOnlyList<InkStroke>> CaptureCurrentInk { get; set; } = () => Array.Empty<InkStroke>();
    public Func<bool> InkManagedExternally { get; set; } = () => false;
    public Action<IReadOnlyList<InkStroke>> ApplyCurrentInk { get; set; }
    public Func<int> MinimumScreenshotStrokeCount { get; set; } = () => 0;
    public Func<bool> AutoSavePresentationInk { get; set; } = () => true;
    public Func<bool> AutoSavePptScreenshots { get; set; } = () => false;
    public Func<bool> NotifyPreviousPage { get; set; } = () => false;
    public Func<bool> NotifyHiddenSlides { get; set; } = () => true;
    public Func<bool> NotifySlideTimings { get; set; } = () => true;
    public Func<string> AutoSaveRoot { get; set; } = () => string.Empty;
    public Func<bool> UseScreenshotDateFolders { get; set; } = () => false;
    public Func<bool> SaveInkWithScreenshot { get; set; } = () => false;

    public PptSnapshot Snapshot => _last;
    public IReadOnlyList<InkStroke> CurrentStrokes => Clone(CaptureCurrentInk());

    /// <summary>
    /// Events request immediate refreshes. Polling covers missed events and hosts that
    /// cannot expose the Office connection point.
    /// </summary>
    public TimeSpan RecommendedPollInterval =>
        _last.IsSlideShowRunning && (!_interop.EventsConnected || _last.IsWindowedShow)
            ? TimeSpan.FromMilliseconds(80) : TimeSpan.FromMilliseconds(900);

    public event Action<PptInkTransition> InkTransition;
    public event Action<PptPresentationNotice> PresentationNotice;
    public event Action<PptScreenshotRequest> ScreenshotRequested;
    public event Action<PptFailure> Failed;

    /// <summary>Attach opportunistically, poll COM, and orchestrate ink changes.</summary>
    public PptSnapshot Poll()
    {
        ThrowIfDisposed();
        if (!_interop.IsAttached) _interop.TryAttach();
        PptSnapshot current = _interop.Poll();
        if (SameSlideState(_last, current))
        {
            // Slides can be added/removed without moving the current slide.
            if (current.IsAttached && current.Presentation?.SlideCount != _last.Presentation?.SlideCount)
                RefreshSlideIndexes(_interop.GetPresentationDetails());
            if (DateTime.UtcNow > _suppressTransitionScreenshotUntilUtc) _suppressTransitionScreenshotUntilUtc = DateTime.MinValue;
            _last = current;
            return current;
        }
        // A full O(slides) COM inventory is only needed when the presentation or its slide
        // count changes; re-reading it on every slide change would delay ink switching.
        if (current.IsAttached &&
            (current.Presentation?.Identity != _last.Presentation?.Identity ||
             current.Presentation?.SlideCount != _last.Presentation?.SlideCount))
            RefreshSlideIndexes(_interop.GetPresentationDetails());
        ProcessTransition(current);
        return current;
    }

    public void RequestScreenshot()
    {
        ThrowIfDisposed();
        if (!_last.IsSlideShowRunning || _last.Slide is null) return;
        RaiseScreenshot(PptScreenshotReason.Explicit);
    }

    public bool RestoreHiddenSlides() => _interop.RestoreHiddenSlides();
    public bool DisableSlideTimings() => _interop.DisableSlideTimings();
    public bool ShowSlideNavigationPane() => _interop.ShowSlideNavigationPane();

    public PptInkArchive CreateArchive()
    {
        ThrowIfDisposed();
        if (_loadedPresentation is null) return null;
        CaptureOutgoing(_last);
        if (_last.IsSlideShowRunning && _last.Slide is not null)
        {
            _lastSlideIndex = _last.Slide.Index > 0 ? _last.Slide.Index : _lastSlideIndex;
            _lastShowPosition = _last.ShowPosition > 0 ? _last.ShowPosition : _lastShowPosition;
        }
        return BuildArchive();
    }

    public void Save()
    {
        ThrowIfDisposed();
        if (_store is null || _loadedPresentation is null || !AutoSavePresentationInk()) return;
        CaptureOutgoing(_last);
        if (_last.IsSlideShowRunning && _last.Slide is not null)
        {
            _lastSlideIndex = _last.Slide.Index > 0 ? _last.Slide.Index : _lastSlideIndex;
            _lastShowPosition = _last.ShowPosition > 0 ? _last.ShowPosition : _lastShowPosition;
        }
        Persist();
    }

    public bool RunSlideShow(PptSlideShowStart start = PptSlideShowStart.PresentationSettings)
    { ThrowIfDisposed(); return _interop.RunSlideShow(start); }
    public bool Next() => NavigateWithScreenshot(PptScreenshotReason.BeforeNavigation, _interop.Next);
    public bool Previous() => NavigateWithScreenshot(PptScreenshotReason.BeforeNavigation, _interop.Previous);
    public bool GoToSlide(int oneBasedIndex) =>
        NavigateWithScreenshot(PptScreenshotReason.BeforeNavigation, () => _interop.GoToSlide(oneBasedIndex));
    public bool ExitSlideShow() { ThrowIfDisposed(); return _interop.ExitSlideShow(); }

    private bool NavigateWithScreenshot(PptScreenshotReason reason, Func<bool> navigate)
    {
        ThrowIfDisposed();
        if (_last.IsSlideShowRunning && AutoSavePptScreenshots() && ShouldSaveScreenshot())
        {
            RaiseScreenshot(reason);
            bool moved = navigate();
            if (moved) _suppressTransitionScreenshotUntilUtc = DateTime.UtcNow.AddSeconds(5);
            return moved;
        }
        return navigate();
    }

    public void Dispose()
    {
        if (_disposed) return;
        Save();
        _interop.Failed -= OnInteropFailed;
        _disposed = true;
    }

    private void ProcessTransition(PptSnapshot current)
    {
        PptSnapshot previous = _last;
        bool wasShow = _showActive;
        bool isShow = current.IsSlideShowRunning;
        bool samePresentation = SamePresentation(_loadedPresentation, current.Presentation);
        bool newPresentation = current.IsAttached && !samePresentation;

        if (_showActive && (!isShow || newPresentation))
        {
            CaptureOutgoing(previous);
            if (AutoSavePresentationInk() && previous.IsSlideShowRunning && previous.Slide is not null)
            {
                _lastSlideIndex = previous.Slide.Index > 0 ? previous.Slide.Index : null;
                _lastShowPosition = previous.ShowPosition > 0 ? previous.ShowPosition : null;
            }
            Persist();
            if (newPresentation || !isShow)
            {
                if (!InkManagedExternally()) ApplyCurrentInk?.Invoke(Array.Empty<InkStroke>());
                RaiseTransition(PptInkTransitionKind.End, previous, current, Array.Empty<InkStroke>());
                _showActive = false;
            }
        }

        if (newPresentation)
        {
            _loadedPresentation = current.Presentation;
            _slides.Clear();
            _slideIndexes.Clear();
            _noticeIdentity = null;
            _lastSlideIndex = null;
            _lastShowPosition = null;
            _legacyFilesDetected = false;
            if (_store is not null)
            {
                try
                {
                    PptInkArchive archive = _store.Load(current.Presentation);
                    if (archive is not null)
                    {
                        if (AutoSavePresentationInk())
                        {
                            foreach (var slide in archive.Slides ?? Array.Empty<PptStoredSlide>())
                            {
                                _slides[slide.SlideId] = Clone(slide.Strokes).ToList();
                                if (slide.SlideIndex > 0 && !_slideIndexes.ContainsKey(slide.SlideId))
                                    _slideIndexes[slide.SlideId] = slide.SlideIndex;
                            }
                        }
                        _lastSlideIndex = archive.LastSlideIndex;
                        _lastShowPosition = archive.LastShowPosition;
                        _legacyFilesDetected = archive.LegacyInkFilesDetected;
                    }
                }
                catch (Exception ex) { Failed?.Invoke(new PptFailure("Load presentation ink", ex.Message, 0)); }
            }
        }

        if (isShow)
        {
            bool began = !_showActive || newPresentation;
            bool slideChanged = previous.IsSlideShowRunning && isShow &&
                (previous.Slide?.SlideId != current.Slide?.SlideId || !SamePresentation(previous.Presentation, current.Presentation));
            if (began)
            {
                _showActive = true;
                // MW_PPT.cs:402 resets the per-show state; a leftover navigation suppression
                // from the previous show must not eat the first external-change screenshot.
                _suppressTransitionScreenshotUntilUtc = DateTime.MinValue;
                ApplySlide(current, previous, PptInkTransitionKind.Begin);
                OfferPresentationNotice(current);
            }
            else if (slideChanged)
            {
                bool suppressDuplicateScreenshot = DateTime.UtcNow <= _suppressTransitionScreenshotUntilUtc;
                _suppressTransitionScreenshotUntilUtc = DateTime.MinValue;
                if (!suppressDuplicateScreenshot && AutoSavePptScreenshots() && ShouldSaveScreenshot())
                    RaiseScreenshot(PptScreenshotReason.ExternalSlideChange, current);
                ApplySlide(current, previous, PptInkTransitionKind.Slide);
            }
        }
        else if (newPresentation && !wasShow)
        {
            OfferPresentationNotice(current);
        }

        _last = current;
    }

    private void ApplySlide(PptSnapshot current, PptSnapshot previous, PptInkTransitionKind kind)
    {
        CaptureOutgoing(previous);
        IReadOnlyList<InkStroke> incoming = current.Slide is not null && _slides.TryGetValue(current.Slide.SlideId, out var found)
            ? found : Array.Empty<InkStroke>();
        if (!InkManagedExternally()) ApplyCurrentInk?.Invoke(Clone(incoming));
        if (current.Slide is not null && current.Slide.Index > 0)
            _slideIndexes[current.Slide.SlideId] = current.Slide.Index;
        _lastSlideIndex = current.Slide?.Index > 0 ? current.Slide.Index : _lastSlideIndex;
        _lastShowPosition = current.ShowPosition > 0 ? current.ShowPosition : _lastShowPosition;
        RaiseTransition(kind, previous, current, incoming);
    }

    private void CaptureOutgoing(PptSnapshot snapshot)
    {
        if (InkManagedExternally()) return;
        if (snapshot?.IsSlideShowRunning != true || snapshot.Slide is null || snapshot.Presentation is null || !_showActive) return;
        if (snapshot.Slide.Index > 0) _slideIndexes[snapshot.Slide.SlideId] = snapshot.Slide.Index;
        try { _slides[snapshot.Slide.SlideId] = Clone(CaptureCurrentInk()).ToList(); }
        catch (Exception ex) { Failed?.Invoke(new PptFailure("Capture outgoing slide ink", ex.Message, 0)); }
    }

    private void RaiseTransition(PptInkTransitionKind kind, PptSnapshot previous, PptSnapshot current,
        IReadOnlyList<InkStroke> incoming)
    {
        if (InkTransition is null) return;
        IReadOnlyList<InkStroke> outgoing = Array.Empty<InkStroke>();
        if (previous?.Slide is not null && _slides.TryGetValue(previous.Slide.SlideId, out var found)) outgoing = Clone(found);
        InkTransition?.Invoke(new PptInkTransition(kind, previous, current, outgoing, Clone(incoming)));
    }

    private void OfferPresentationNotice(PptSnapshot snapshot)
    {
        if (!snapshot.IsAttached || _noticeIdentity == snapshot.Presentation?.Identity) return;
        bool previousEnabled = NotifyPreviousPage();
        PptPresentationDetails details = _interop.GetPresentationDetails();
        if (details is null) return;
        _noticeIdentity = snapshot.Presentation?.Identity;
        int? resume = previousEnabled ? _lastSlideIndex : null;
        if (previousEnabled && resume is > 0 && !details.Slides.Any(s => s.Index == resume.Value)) resume = null;
        bool hidden = NotifyHiddenSlides() && details.HasHiddenSlides;
        bool timings = NotifySlideTimings() && details.HasSlideTimings;
        PresentationNotice?.Invoke(new PptPresentationNotice(details, resume, hidden, timings, _legacyFilesDetected));
    }

    private bool ShouldSaveScreenshot()
    {
        try { return CaptureCurrentInk().Count > Math.Max(0, MinimumScreenshotStrokeCount()); }
        catch { return false; }
    }

    private void RaiseScreenshot(PptScreenshotReason reason, PptSnapshot snapshot = null)
    {
        snapshot ??= _last;
        if (snapshot.Slide is null || snapshot.Presentation is null) return;
        string path = null;
        string root = AutoSaveRoot() ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(root))
        {
            string folder = Path.Combine(root, "Auto Saved - PPT Screenshots");
            if (UseScreenshotDateFolders()) folder = Path.Combine(folder, DateTime.Now.ToString("yyyy-MM-dd"));
            string presentationPart = SafeFilePart(snapshot.Presentation.Name);
            int position = snapshot.ShowPosition > 0 ? snapshot.ShowPosition : snapshot.Slide.Index;
            int filePosition = reason == PptScreenshotReason.ExternalSlideChange && _last.ShowPosition > 0
                ? _last.ShowPosition : position;
            string imageName = reason == PptScreenshotReason.Explicit
                ? $"{position}_{DateTime.Now:HH-mm-ss}"
                : filePosition.ToString("0");
            path = Path.Combine(folder, presentationPart, imageName + ".png");
        }
        PptSlideInfo inkSlide = reason == PptScreenshotReason.ExternalSlideChange ? _last.Slide : snapshot.Slide;
        ScreenshotRequested?.Invoke(new PptScreenshotRequest(snapshot, inkSlide, Clone(CaptureCurrentInk()), reason,
            path, SaveInkWithScreenshot()));
    }

    private void Persist()
    {
        if (InkManagedExternally()) return;
        if (_store is null || _loadedPresentation is null || !AutoSavePresentationInk()) return;
        try { _store.Save(BuildArchive()); }
        catch (Exception ex) { Failed?.Invoke(new PptFailure("Save presentation ink", ex.Message, 0)); }
    }

    private PptInkArchive BuildArchive() => new(_loadedPresentation, _lastSlideIndex, _lastShowPosition,
        _slides.Select(pair => new PptStoredSlide(pair.Key,
            _slideIndexes.TryGetValue(pair.Key, out int index) ? index : 0, Clone(pair.Value))).ToList(),
        _legacyFilesDetected);

    private void RefreshSlideIndexes(PptPresentationDetails details)
    {
        if (details is null) return;
        foreach (PptSlideInfo slide in details.Slides)
        {
            if (slide.SlideId > 0 && slide.Index > 0) _slideIndexes[slide.SlideId] = slide.Index;
        }
    }

    private static bool SamePresentation(PptPresentationInfo a, PptPresentationInfo b)
    {
        if (a is null || b is null) return a is null && b is null;
        if (string.Equals(a.Identity, b.Identity, StringComparison.Ordinal)) return true;
        if (a.HasSavedPath && b.HasSavedPath && !string.IsNullOrWhiteSpace(a.FullName) && !string.IsNullOrWhiteSpace(b.FullName))
            return string.Equals(a.FullName, b.FullName, StringComparison.OrdinalIgnoreCase);
        return !a.HasSavedPath && !b.HasSavedPath && a.SlideCount == b.SlideCount &&
            string.Equals(a.Name, b.Name, StringComparison.Ordinal) && a.SlideWidth == b.SlideWidth && a.SlideHeight == b.SlideHeight;
    }

    private static bool SameSlideState(PptSnapshot a, PptSnapshot b) =>
        a.State == b.State && SamePresentation(a.Presentation, b.Presentation) &&
        a.Slide?.SlideId == b.Slide?.SlideId && a.Slide?.Index == b.Slide?.Index &&
        a.ShowPosition == b.ShowPosition && a.WindowHandle == b.WindowHandle;

    private static IReadOnlyList<InkStroke> Clone(IEnumerable<InkStroke> strokes) =>
        strokes?.Where(s => s is not null).Select(s => s.Clone()).ToList() ?? new List<InkStroke>();

    private static string SafeFilePart(string value)
    {
        string result = string.IsNullOrWhiteSpace(value) ? "Presentation" : value;
        foreach (char c in Path.GetInvalidFileNameChars()) result = result.Replace(c, '_');
        return result.Replace('/', '_').Replace('\\', '_');
    }

    private void OnInteropFailed(PptFailure failure) => Failed?.Invoke(failure);

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(PptInkSession));
    }
}
