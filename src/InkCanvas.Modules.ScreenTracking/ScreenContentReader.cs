using System.Diagnostics;
using System.Globalization;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using InkCanvas.Core.Ink;
using InkCanvas.Core.ScreenTracking;
using InkCanvas.Core.Settings;
using InkCanvas.Platform.Interop;
using Interop.UIAutomationClient;

namespace InkCanvas.Platform.System;

/// <summary>Read-only accessibility/Office sampling on a dedicated worker. Never runs on the ink thread.</summary>
public sealed class ScreenContentReader : IDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private readonly Func<nint> _foregroundWindow;
    private ExperimentalSettings _settings = new();
    private ScreenViewSnapshot _latest;
    private long _lastSampleTicks;
    private bool _disposed;
    private readonly int _ownPid = Environment.ProcessId;
    private IUIAutomation _automation;
    private IUIAutomationElement _document;
    private nint _target;
    private string _lastContext;
    private float _zoom = 1;
    private readonly List<(IUIAutomationTextRange Range, Rect Bounds, double Font)> _textAnchors = new();
    private Vector2 _estimatedScroll;
    private bool _hasEstimate;
    private bool _hasZoom, _alignmentLost;
    private readonly WordScrollAlignment _wordScroll = new();
    private float _wordAnchorScale;
    private bool _wordAnchorPending;
    private Vector2 _wordAnchorScroll, _wordAnchorOrigin;
    private long _fastObservationUntil;
    private Rect _previousViewport;
    private int _requestedEpoch, _epoch;
    private readonly ContentPageVariants _variants = new();
    private long _lastContentRead, _lastDocumentRead, _lastAddressRead;
    private string _contentText, _address;
    private IReadOnlyList<ScreenRegionAnchor> _stableRegions = Array.Empty<ScreenRegionAnchor>();
    private Matrix3x2 _stableRegionsView = Matrix3x2.Identity;
    private bool _hadOfficeView;

    public ScreenViewSnapshot Latest => Volatile.Read(ref _latest);
    public string LastOfficeError { get; private set; }
    public TimeSpan SampleAge => Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastSampleTicks));
    public int ResetAnchor() { int epoch = Interlocked.Increment(ref _requestedEpoch); _wake.Set(); return epoch; }

    public ScreenContentReader() : this(NativeMethods.GetForegroundWindow) { }

    public ScreenContentReader(Func<nint> foregroundWindow)
    {
        _foregroundWindow = foregroundWindow ?? throw new ArgumentNullException(nameof(foregroundWindow));
        _thread = new Thread(ReadLoop) { IsBackground = true, Name = "InkCanvas screen-content observer" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
    }

    public void Configure(ExperimentalSettings settings)
    {
        Volatile.Write(ref _settings, settings ?? new ExperimentalSettings());
        if (!_disposed) _wake.Set();
    }

    private void ReadLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var settings = Volatile.Read(ref _settings);
                if (!settings.ScreenContentTracking) { ResetTarget(); _wake.WaitOne(300); continue; }
                try
                {
                    int epoch = Volatile.Read(ref _requestedEpoch);
                    if (epoch != _epoch) { ReleaseDocument(); _lastContext = null; _epoch = epoch; }
                    var snapshot = ReadForeground(settings);
                    if (snapshot is not null)
                    {
                        Volatile.Write(ref _latest, snapshot with { ObservationEpoch = _epoch });
                        Interlocked.Exchange(ref _lastSampleTicks, Stopwatch.GetTimestamp());
                    }
                }
                catch (Exception)
                {
                    ReleaseDocument();
                    Volatile.Write(ref _latest, new ScreenViewSnapshot { Application = ApplicationFor(_target),
                        WindowHandle = _target, Status = "正在等待应用重新提供正文信息" });
                    Interlocked.Exchange(ref _lastSampleTicks, Stopwatch.GetTimestamp());
                }
                _wake.WaitOne(Stopwatch.GetTimestamp() < _fastObservationUntil ? 25 : 100);
            }
        }
        finally { ResetTarget(); Release(_automation); _wake.Dispose(); _stop.Dispose(); }
    }

    private ScreenViewSnapshot ReadForeground(ExperimentalSettings settings)
    {
        nint window = _foregroundWindow();
        NativeMethods.GetWindowThreadProcessId(window, out uint pid);
        if (pid == _ownPid) window = _target;
        var application = ApplicationFor(window);
        bool enabled = application switch
        {
            TrackedApplication.Word => settings.Word,
            TrackedApplication.PowerPoint => settings.PowerPoint,
            TrackedApplication.Edge => settings.Edge,
            _ => false,
        };
        if (!enabled || window == 0 || !NativeMethods.IsWindowVisible(window) || IsIconic(window))
        {
            ResetTarget();
            return new ScreenViewSnapshot { Status = "当前界面未启用适配，已保留各页面墨迹" };
        }
        if (_target != window) { ResetTarget(); _target = window; }
        _automation ??= (IUIAutomation)new CUIAutomation8();
        if (_automation is IUIAutomation2 automation2) { automation2.ConnectionTimeout = 200; automation2.TransactionTimeout = 250; }
        var root = _automation.ElementFromHandle(window);
        try
        {
            if (!TryGetClientBounds(window, out var client)) return null;
            if (_document is null || _document.CurrentIsOffscreen != 0 || Stopwatch.GetElapsedTime(_lastDocumentRead).TotalMilliseconds >= 600)
            {
                var next = FindDocument(root, client, application);
                if (_document is not null && next is not null && _automation.CompareElements(_document, next) != 0)
                    Release(next);
                else { ReleaseDocument(); _document = next; }
                _lastDocumentRead = Stopwatch.GetTimestamp();
            }
            if (_document is null && application == TrackedApplication.PowerPoint) _document = _automation.ElementFromHandle(window);
            if (_document is null) return new ScreenViewSnapshot { Application = application, WindowHandle = window,
                Status = "未识别到可跟随的正文区域，暂时隐藏该页批注" };
            Rect viewport = Intersection(Bounds(_document), client);
            if (viewport.Width < 80 || viewport.Height < 60) { ReleaseDocument(); return null; }
            string identity = DocumentIdentity(root, _document, application, window);
            float? officeZoom = null;
            Vector2? officeScroll = null;
            if (application is TrackedApplication.Word or TrackedApplication.PowerPoint)
            {
                var office = OfficeScreenView.Read(application, window, application == TrackedApplication.PowerPoint ? client : viewport, out var error);
                LastOfficeError = error;
                if (office is not null)
                {
                    _hadOfficeView = true;
                    identity = office.ContextKey;
                    if (office.HasZoomMetrics) officeZoom = office.Scale;
                    if (office.HasScrollMetrics) officeScroll = office.ScrollPixels;
                    if (!office.Viewport.IsEmpty)
                    {
                        viewport = Intersection(viewport, office.Viewport);
                        if (officeScroll.HasValue)
                            officeScroll += new Vector2(viewport.Left - office.Viewport.Left, viewport.Top - office.Viewport.Top);
                    }
                }
                else if (_hadOfficeView || application == TrackedApplication.PowerPoint)
                    return new ScreenViewSnapshot { Application = application, WindowHandle = window,
                        Status = "正在等待 Office 重新提供当前页面视图信息" };
            }
            bool contextChanged = identity != _lastContext;
            if (contextChanged)
            {
                ClearTextAnchors(); _lastContext = identity;
                _zoom = officeZoom ?? GetDpi(window) / 96f; _estimatedScroll = Vector2.Zero; _hasEstimate = false;
                _alignmentLost = false; _hasZoom = officeZoom.HasValue; _previousViewport = viewport;
                _wordScroll.Reset();
                _wordAnchorScale = 0;
            }
            float previousZoom = _zoom;
            bool zoomKnown = officeZoom.HasValue;
            if (officeZoom.HasValue) _zoom = officeZoom.Value;
            else if (Pattern<IUIAutomationTransformPattern2>(_document, 10028) is { } transform)
            {
                try { if (transform.CurrentCanZoom != 0) { _zoom = (float)transform.CurrentZoomLevel / 100 * GetDpi(window) / 96f; zoomKnown = true; } }
                finally { Release(transform); }
            }

            var anchorMovement = MeasureTextAnchors(previousZoom, viewport, application, ref zoomKnown, out var wordAnchorScroll);
            _hasZoom |= zoomKnown;
            bool scrollKnown = false, absoluteScroll = false;
            Vector2 scroll = _estimatedScroll;
            var scrollPattern = FindScrollPattern(_document);
            if (scrollPattern is not null)
            {
                try
                {
                    double verticalView = scrollPattern.CurrentVerticalViewSize, horizontalView = scrollPattern.CurrentHorizontalViewSize;
                    scroll = new Vector2(ScreenViewSnapshot.ScrollOffset(viewport.Width, horizontalView, scrollPattern.CurrentHorizontalScrollPercent),
                        ScreenViewSnapshot.ScrollOffset(viewport.Height, verticalView, scrollPattern.CurrentVerticalScrollPercent));
                    scrollKnown = verticalView is > 0 and <= 100 && horizontalView is > 0 and <= 100;
                    absoluteScroll = scrollKnown;
                }
                finally { Release(scrollPattern); }
            }
            if (officeScroll.HasValue) { scroll = officeScroll.Value; scrollKnown = absoluteScroll = true; }
            if (application == TrackedApplication.Word && !scrollKnown && TryNativeScroll(window, viewport, out var wordNativeScroll))
            { scroll = wordNativeScroll; scrollKnown = absoluteScroll = true; }
            if (application == TrackedApplication.Word)
            {
                bool scrollJump = _wordScroll.HasScrollJump(absoluteScroll ? scroll : null, viewport.Height / 2);
                if (scrollJump && Math.Abs(_zoom / previousZoom - 1) < .002f)
                {
                    // Word can keep old character rectangles alive after a page-sized jump.
                    // Reacquire visible ranges against the confirmed scrollbar displacement.
                    wordAnchorScroll = null;
                    anchorMovement = null;
                    _wordAnchorPending = false;
                }
                if (_hasEstimate && !wordAnchorScroll.HasValue && (Math.Abs(_zoom - previousZoom) > .002f || _wordAnchorPending && !scrollJump))
                {
                    // Keep the reference through a pinch's incomplete accessibility frames.
                    // A confirmed page-sized scroll can instead replace ranges that left the screen.
                    _zoom = previousZoom;
                    _fastObservationUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
                    return null;
                }
                if (wordAnchorScroll.HasValue)
                    anchorMovement = _estimatedScroll * (_zoom / previousZoom) - wordAnchorScroll.Value;
                var aligned = _wordScroll.Update(_estimatedScroll, _zoom / previousZoom, anchorMovement,
                    absoluteScroll ? scroll : null, _hasEstimate, referenceAligned: wordAnchorScroll.HasValue);
                scroll = aligned.Scroll;
                scrollKnown = aligned.HasMetrics;
                _alignmentLost = !aligned.Reliable;
            }
            else if (!scrollKnown && anchorMovement.HasValue)
            {
                scroll = _estimatedScroll * (_zoom / previousZoom) - anchorMovement.Value;
                scrollKnown = true;
            }
            if (application != TrackedApplication.Word)
            {
                if (!scrollKnown && TryNativeScroll(window, viewport, out var nativeScroll)) { scroll = nativeScroll; scrollKnown = absoluteScroll = true; }
                if (absoluteScroll) _alignmentLost = false;
                else if (!scrollKnown && _hasEstimate) _alignmentLost = true;
            }
            bool moving = Vector2.Distance(scroll, _estimatedScroll) > 2 || Math.Abs(_zoom - previousZoom) > .002f;
            if (moving) _fastObservationUntil = Stopwatch.GetTimestamp() + Stopwatch.Frequency / 2;
            _estimatedScroll = scroll;
            _hasEstimate |= scrollKnown;
            if (_textAnchors.Count == 0 || anchorMovement is null)
            {
                RefreshTextAnchors(viewport, application == TrackedApplication.Word);
                _wordAnchorScale = _zoom;
                _wordAnchorScroll = scroll;
                _wordAnchorOrigin = new Vector2(viewport.Left, viewport.Top);
            }
            _previousViewport = viewport;
            var contentView = Matrix3x2.CreateScale(_zoom) * Matrix3x2.CreateTranslation(new Vector2(viewport.Left, viewport.Top) - scroll);
            if (contextChanged || !moving && Stopwatch.GetElapsedTime(_lastContentRead).TotalMilliseconds >= 600)
            {
                ReadContent(settings.PreserveUnchangedRegions, application is TrackedApplication.Edge or TrackedApplication.Word);
                _stableRegionsView = contentView;
                _lastContentRead = Stopwatch.GetTimestamp();
            }
            // Classify only fresh text samples. Repeated timer frames must not confirm a loading page.
            string page = application is TrackedApplication.Edge or TrackedApplication.Word && settings.RememberPages
                ? _variants.Resolve(identity, _contentText, moving) : identity;
            _contentText = null;
            return new ScreenViewSnapshot
            {
                Application = application, WindowHandle = window, ContextKey = page,
                Viewport = viewport, Scale = _zoom, ScrollPixels = scroll,
                HasScrollMetrics = scrollKnown || _hasEstimate, HasZoomMetrics = _hasZoom,
                AlignmentReliable = !_alignmentLost,
                Anchors = ReadFixedCandidates(viewport), Occlusions = ReadOccludingWindows(window, viewport),
                StableContentRegions = settings.PreserveUnchangedRegions ? MoveRegions(contentView) : Array.Empty<ScreenRegionAnchor>(),
                Status = _alignmentLost ? $"{application} · 定位已中断，请重新绑定当前批注位置"
                    : scrollKnown ? $"{application} · 正文跟随中" : $"{application} · 未取得连续滚动数据，保持最近位置",
            };
        }
        finally { Release(root); }
    }

    private IUIAutomationElement FindDocument(IUIAutomationElement root, Rect client, TrackedApplication application)
    {
        var condition = _automation.CreatePropertyCondition(30003, 50030); // ControlType.Document
        var documents = root.FindAll(TreeScope.TreeScope_Descendants, condition);
        IUIAutomationElement best = null;
        float area = 0;
        try
        {
            for (int index = 0; index < Math.Min(documents.Length, 24); index++)
            {
                var candidate = documents.GetElement(index);
                var bounds = Intersection(Bounds(candidate), client);
                float candidateArea = bounds.Width * bounds.Height;
                if (candidate.CurrentIsOffscreen == 0 && candidateArea > area)
                {
                    Release(best); best = candidate; area = candidateArea;
                }
                else Release(candidate);
            }
        }
        finally { Release(documents); Release(condition); }
        // Office also hosts the slide/text canvas as a native pane in some versions.
        if (application == TrackedApplication.PowerPoint || best is null && application == TrackedApplication.Word)
        {
            nint child = 0;
            EnumChildWindows(_target, (handle, _) =>
            {
                var name = new StringBuilder(128); GetClassNameW(handle, name, name.Capacity);
                string value = name.ToString();
                if (NativeMethods.IsWindowVisible(handle) && OfficeScreenView.IsDocumentPane(application, value) && TryGetClientBounds(handle, out var bounds)
                    && bounds.Width * bounds.Height > area) { child = handle; area = bounds.Width * bounds.Height; }
                return true;
            }, 0);
            if (child != 0) { Release(best); best = _automation.ElementFromHandle(child); }
        }
        return best;
    }

    private string DocumentIdentity(IUIAutomationElement root, IUIAutomationElement document, TrackedApplication application, nint window)
    {
        string value = null;
        var legacy = Pattern<IUIAutomationLegacyIAccessiblePattern>(document, 10018);
        try { value = legacy?.CurrentValue; } catch { } finally { Release(legacy); }
        if (application == TrackedApplication.Edge && !IsDocumentUrl(value)
            && Stopwatch.GetElapsedTime(_lastAddressRead).TotalMilliseconds >= 500)
        {
            var condition = _automation.CreatePropertyCondition(30003, 50004);
            var edits = root.FindAll(TreeScope.TreeScope_Descendants, condition);
            try
            {
                for (int index = 0; index < Math.Min(edits.Length, 16); index++)
                {
                    var edit = edits.GetElement(index);
                    try
                    {
                        string id = edit.CurrentAutomationId ?? "", name = edit.CurrentName ?? "";
                        if (!id.Contains("address", StringComparison.OrdinalIgnoreCase) && !name.Contains("地址")
                            && !name.Contains("Address", StringComparison.OrdinalIgnoreCase)) continue;
                        var pattern = Pattern<IUIAutomationValuePattern>(edit, 10002);
                        try { value = pattern?.CurrentValue; } finally { Release(pattern); }
                        if (!string.IsNullOrWhiteSpace(value)) break;
                    }
                    finally { Release(edit); }
                }
            }
            finally { Release(edits); Release(condition); }
            _address = value; _lastAddressRead = Stopwatch.GetTimestamp();
        }
        if (application == TrackedApplication.Edge && !IsDocumentUrl(value) && !string.IsNullOrWhiteSpace(_address)) value = _address;
        if (string.IsNullOrWhiteSpace(value)) value = document.CurrentName;
        if (string.IsNullOrWhiteSpace(value)) value = string.Join('.', document.GetRuntimeId());
        return $"{application}:{window}:{Hash(value)}";
    }

    private Vector2? MeasureTextAnchors(float previousZoom, Rect viewport, TrackedApplication application,
        ref bool zoomKnown, out Vector2? wordScroll)
    {
        wordScroll = null;
        _wordAnchorPending = false;
        var offsets = new List<Vector2>();
        var zoomRatios = new List<float>();
        var measurements = new List<(IUIAutomationTextRange Range, Rect PreviousBounds, Rect Bounds, double Font)>();
        foreach (var anchor in _textAnchors)
        {
            var rect = TextBounds(anchor.Range);
            if (rect.IsEmpty)
            {
                measurements.Add((anchor.Range, anchor.Bounds, rect, anchor.Font));
                continue;
            }
            double font = application == TrackedApplication.Word ? anchor.Font
                : anchor.Range.GetAttributeValue(40005) is double size ? size : anchor.Font;
            measurements.Add((anchor.Range, anchor.Bounds, rect, font));
            if (!zoomKnown && anchor.Font > 0 && font > 0)
            {
                float fontRatio = (float)(font / anchor.Font);
                // Chromium may expose CSS font size while its physical glyph bounds change with zoom.
                if (Math.Abs(fontRatio - 1) < .005f && anchor.Bounds.Height > 2) fontRatio = rect.Height / anchor.Bounds.Height;
                zoomRatios.Add(fontRatio);
            }
        }
        if (application == TrackedApplication.Word && _wordAnchorScale > 0)
        {
            var pairs = measurements.Where(item => !item.Bounds.IsEmpty && item.Bounds.Top > viewport.Top + 2
                && item.Bounds.Bottom < viewport.Bottom - 2).Select(item => (item.PreviousBounds, item.Bounds)).ToArray();
            if (TextAnchorAlignment.TryFit(pairs, out var transform))
            {
                _zoom = _wordAnchorScale * transform.M11;
                wordScroll = _wordAnchorScroll * transform.M11 + new Vector2(viewport.Left, viewport.Top)
                    - _wordAnchorOrigin * transform.M11 - transform.Translation;
                zoomKnown = true;
            }
            else _wordAnchorPending = pairs.Length >= 2;
            // Word keeps an absolute reference through transient mismatches and successive zoom frames.
            return null;
        }
        if (zoomRatios.Count >= 2 && zoomRatios.Max() - zoomRatios.Min() < .02f && zoomRatios[0] is >= .25f and <= 4)
        {
            _zoom *= zoomRatios.Average(); zoomKnown = true;
        }
        float ratio = _zoom / previousZoom;
        _textAnchors.Clear();
        foreach (var measurement in measurements)
        {
            var current = measurement.Bounds;
            if (current.IsEmpty) { Release(measurement.Range); continue; }
            // A stable character range follows content even when scroll percentages are absent.
            offsets.Add(new Vector2(current.Left - viewport.Left - (measurement.PreviousBounds.Left - _previousViewport.Left) * ratio,
                current.Top - viewport.Top - (measurement.PreviousBounds.Top - _previousViewport.Top) * ratio));
            // Keep the exact bounds used for this observation as the next observation's baseline.
            _textAnchors.Add((measurement.Range, current, measurement.Font));
        }
        if (offsets.Count == 0) return null;
        var average = new Vector2(offsets.Average(point => point.X), offsets.Average(point => point.Y));
        return offsets.All(offset => Vector2.Distance(offset, average) < 4) ? average : null;
    }

    private void RefreshTextAnchors(Rect viewport, bool word)
    {
        ClearTextAnchors();
        var text = Pattern<IUIAutomationTextPattern>(_document, 10014);
        if (text is null) return;
        try
        {
            var positions = new List<Vector2>();
            foreach (float portion in new[] { .15f, .3f, .5f, .7f, .85f })
                positions.Add(new Vector2(viewport.Center.X, viewport.Top + viewport.Height * portion));
            if (word)
            {
                positions.Add(new Vector2(viewport.Left + viewport.Width * .25f, viewport.Center.Y));
                positions.Add(new Vector2(viewport.Left + viewport.Width * .75f, viewport.Center.Y));
            }
            foreach (var position in positions)
            {
                var range = text.RangeFromPoint(new tagPOINT { x = (int)position.X, y = (int)position.Y });
                range.MoveEndpointByRange(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, range, TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start);
                range.MoveEndpointByUnit(TextPatternRangeEndpoint.TextPatternRangeEndpoint_End, TextUnit.TextUnit_Character, 1);
                var bounds = TextBounds(range);
                if (bounds.IsEmpty || _textAnchors.Any(anchor => anchor.Bounds == bounds)) { Release(range); continue; }
                double font = range.GetAttributeValue(40005) is double size ? size : 0;
                _textAnchors.Add((range, bounds, font));
            }
        }
        catch { ClearTextAnchors(); }
        finally { Release(text); }
    }

    private IReadOnlyList<ScreenRegionAnchor> ReadFixedCandidates(Rect viewport)
    {
        var result = new List<ScreenRegionAnchor>();
        var walker = _automation.ControlViewWalker;
        var pending = new Queue<(IUIAutomationElement Element, int Depth)>();
        var first = walker.GetFirstChildElement(_document);
        if (first is not null) pending.Enqueue((first, 0));
        var watch = Stopwatch.StartNew();
        try
        {
            while (pending.Count > 0 && result.Count < 32 && watch.ElapsedMilliseconds < 40)
            {
                var (element, depth) = pending.Dequeue();
                try
                {
                    var bounds = Intersection(Bounds(element), viewport);
                    int kind = element.CurrentControlType;
                    if (!bounds.IsEmpty && kind is 50021 or 50026 or 50033 or 50034)
                    {
                        string key = kind + ":" + element.CurrentAutomationId + ":" + Hash(element.CurrentName ?? "");
                        result.Add(new ScreenRegionAnchor(key, bounds));
                    }
                    var next = walker.GetNextSiblingElement(element); if (next is not null) pending.Enqueue((next, depth));
                    if (depth < 2) { var child = walker.GetFirstChildElement(element); if (child is not null) pending.Enqueue((child, depth + 1)); }
                }
                finally { Release(element); }
            }
        }
        finally { foreach (var item in pending) Release(item.Element); Release(walker); }
        return result;
    }


    private void ReadContent(bool paragraphs, bool fingerprint)
    {
        _contentText = null;
        _stableRegions = Array.Empty<ScreenRegionAnchor>();
        var pattern = Pattern<IUIAutomationTextPattern>(_document, 10014);
        if (pattern is null) return;
        try
        {
            if (fingerprint)
            {
                var document = pattern.DocumentRange;
                try { _contentText = document.GetText(4096); }
                finally { Release(document); }
            }
            if (!paragraphs) return;
            var visible = pattern.GetVisibleRanges();
            var regions = new List<ScreenRegionAnchor>();
            var watch = Stopwatch.StartNew();
            try
            {
                for (int i = 0; i < Math.Min(visible.Length, 12) && regions.Count < 24 && watch.ElapsedMilliseconds < 60; i++)
                {
                    var range = visible.GetElement(i);
                    IUIAutomationTextRange paragraph = null;
                    try
                    {
                        paragraph = range.Clone();
                        paragraph.ExpandToEnclosingUnit(TextUnit.TextUnit_Paragraph);
                        for (int count = 0; count < 24 && regions.Count < 24 && watch.ElapsedMilliseconds < 60; count++)
                        {
                            if (paragraph.CompareEndpoints(TextPatternRangeEndpoint.TextPatternRangeEndpoint_Start,
                                range, TextPatternRangeEndpoint.TextPatternRangeEndpoint_End) >= 0) break;
                            string text = paragraph.GetText(513)?.Trim();
                            var bounds = TextBounds(paragraph, allRectangles: true);
                            if (text is { Length: >= 12 and <= 512 } && !bounds.IsEmpty)
                                regions.Add(new ScreenRegionAnchor(Hash(text), bounds));
                            if (paragraph.Move(TextUnit.TextUnit_Paragraph, 1) == 0) break;
                        }
                    }
                    finally { Release(paragraph); Release(range); }
                }
            }
            finally { Release(visible); }
            _stableRegions = regions.GroupBy(region => region.Key).Where(group => group.Count() == 1).Select(group => group.First()).ToArray();
        }
        catch { _stableRegions = Array.Empty<ScreenRegionAnchor>(); }
        finally { Release(pattern); }
    }

    private IReadOnlyList<ScreenRegionAnchor> MoveRegions(Matrix3x2 view)
    {
        if (_stableRegionsView == view || !Matrix3x2.Invert(_stableRegionsView, out var inverse)) return _stableRegions;
        var delta = inverse * view;
        return _stableRegions.Select(region =>
        {
            var a = Vector2.Transform(new Vector2(region.Bounds.Left, region.Bounds.Top), delta);
            var b = Vector2.Transform(new Vector2(region.Bounds.Right, region.Bounds.Bottom), delta);
            return new ScreenRegionAnchor(region.Key, new Rect(a.X, a.Y, b.X, b.Y));
        }).ToArray();
    }

    private IUIAutomationScrollPattern FindScrollPattern(IUIAutomationElement document)
    {
        var pattern = Pattern<IUIAutomationScrollPattern>(document, 10004);
        if (pattern is not null) return pattern;
        var walker = _automation.ControlViewWalker;
        IUIAutomationElement parent = null;
        try
        {
            parent = walker.GetParentElement(document);
            for (int index = 0; parent is not null && index < 4; index++)
            {
                pattern = Pattern<IUIAutomationScrollPattern>(parent, 10004);
                if (pattern is not null) return pattern;
                var next = walker.GetParentElement(parent); Release(parent); parent = next;
            }
            return null;
        }
        finally { Release(parent); Release(walker); }
    }

    private static T Pattern<T>(IUIAutomationElement element, int id) where T : class
    {
        try { return element?.GetCurrentPattern(id) as T; } catch { return null; }
    }
    private static Rect Bounds(IUIAutomationElement element)
    {
        var rect = element.CurrentBoundingRectangle;
        return new Rect(rect.left, rect.top, rect.right, rect.bottom);
    }
    private static Rect TextBounds(IUIAutomationTextRange range, bool allRectangles = false)
    {
        try
        {
            var rectangles = range.GetBoundingRectangles();
            var result = Rect.Empty;
            for (int i = 0; rectangles is not null && i + 3 < rectangles.Length; i += 4)
            {
                var rect = new Rect((float)rectangles[i], (float)rectangles[i + 1],
                    (float)(rectangles[i] + rectangles[i + 2]), (float)(rectangles[i + 1] + rectangles[i + 3]));
                if (!rect.IsEmpty) result = result.IsEmpty ? rect : new Rect(Math.Min(result.Left, rect.Left), Math.Min(result.Top, rect.Top), Math.Max(result.Right, rect.Right), Math.Max(result.Bottom, rect.Bottom));
                if (!allRectangles) break;
            }
            return result;
        }
        catch { return Rect.Empty; }
    }
    private static bool IsDocumentUrl(string value) => !string.IsNullOrWhiteSpace(value) && (value.Contains("://") || value.StartsWith("file:", StringComparison.OrdinalIgnoreCase));
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? "")));
    private static uint GetDpi(nint window) { uint dpi = NativeMethods.GetDpiForWindow(window); return dpi == 0 ? 96u : dpi; }
    private static TrackedApplication ApplicationFor(nint window)
    {
        try
        {
            NativeMethods.GetWindowThreadProcessId(window, out uint pid);
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName.ToLowerInvariant() switch { "winword" => TrackedApplication.Word,
                "powerpnt" => TrackedApplication.PowerPoint, "msedge" => TrackedApplication.Edge, _ => TrackedApplication.None };
        }
        catch { return TrackedApplication.None; }
    }
    private static bool TryGetClientBounds(nint window, out Rect bounds)
    {
        bounds = Rect.Empty;
        if (!GetClientRect(window, out var rect)) return false;
        var origin = new NativeMethods.POINT(); ClientToScreen(window, ref origin);
        bounds = new Rect(origin.X, origin.Y, origin.X + rect.Right, origin.Y + rect.Bottom);
        return !bounds.IsEmpty;
    }
    public static Rect Intersection(Rect a, Rect b) => new(Math.Max(a.Left, b.Left), Math.Max(a.Top, b.Top), Math.Min(a.Right, b.Right), Math.Min(a.Bottom, b.Bottom));
    private IReadOnlyList<Rect> ReadOccludingWindows(nint target, Rect viewport)
    {
        var masks = new List<Rect>();
        for (nint window = NativeMethods.GetWindow(target, 3); window != 0 && masks.Count < 32; window = NativeMethods.GetWindow(window, 3))
        {
            NativeMethods.GetWindowThreadProcessId(window, out uint pid);
            if (pid == _ownPid || !NativeMethods.IsWindowVisible(window) || IsIconic(window) || !GetWindowRect(window, out var rect)) continue;
            if (DwmGetWindowAttribute(window, 14, out int cloaked, sizeof(int)) == 0 && cloaked != 0) continue;
            var mask = Intersection(new Rect(rect.Left, rect.Top, rect.Right, rect.Bottom), viewport);
            if (!mask.IsEmpty) masks.Add(mask);
        }
        return masks;
    }
    private static bool TryNativeScroll(nint root, Rect viewport, out Vector2 position)
    {
        var result = Vector2.Zero; bool available = false;
        EnumChildWindows(root, (window, _) =>
        {
            if (!TryGetClientBounds(window, out var client) || Intersection(client, viewport).Width < viewport.Width * .8f) return true;
            var info = new SCROLLINFO { cbSize = (uint)Marshal.SizeOf<SCROLLINFO>(), fMask = 0x17 };
            if (GetScrollInfo(window, 1, ref info) && info.nPage > 0 && info.nMax > info.nMin)
            {
                result.Y = (info.nPos - info.nMin) * viewport.Height / info.nPage; available = true;
            }
            return !available;
        }, 0);
        position = result; return available;
    }
    private void ClearTextAnchors() { foreach (var anchor in _textAnchors) Release(anchor.Range); _textAnchors.Clear(); }
    private void ReleaseDocument()
    {
        ClearTextAnchors(); Release(_document); _document = null;
        _stableRegions = Array.Empty<ScreenRegionAnchor>(); _lastContentRead = 0;
    }
    private void ResetTarget()
    {
        ReleaseDocument(); _target = 0; _lastContext = null; _hasEstimate = false; _address = null;
        _lastAddressRead = 0; _lastDocumentRead = 0;
        _hadOfficeView = false;
    }
    private static void Release(object value) { if (value is not null && Marshal.IsComObject(value)) Marshal.ReleaseComObject(value); }
    public void Dispose() { if (_disposed) return; _disposed = true; _stop.Cancel(); _wake.Set(); _thread.Join(300); }

    private delegate bool WindowCallback(nint window, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumChildWindows(nint window, WindowCallback callback, nint parameter);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint window, StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint window, out NativeMethods.RECT rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeMethods.RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint window, ref NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool GetScrollInfo(nint window, int bar, ref SCROLLINFO info);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
    [StructLayout(LayoutKind.Sequential)] private struct SCROLLINFO { public uint cbSize, fMask; public int nMin, nMax; public uint nPage; public int nPos, nTrackPos; }
}
