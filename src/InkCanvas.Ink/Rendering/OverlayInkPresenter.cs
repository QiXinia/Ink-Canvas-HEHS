using InkCanvas.Core.Diagnostics;
using InkCanvas.Core.Imaging;
using InkCanvas.Core.ScreenTracking;
using InkCanvas.Core.Ink;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.DirectX;
using Windows.Storage.Streams;
using InkCanvas.Platform.Windows;

namespace InkCanvas.Ink.Rendering;

/// <summary>
/// Renders the ink scene into a premultiplied-alpha surface and pushes it to a
/// <see cref="LayeredInkWindow"/>. This is the transparent screen-annotation path.
/// <para>
/// Latency: the whole chain runs synchronously inside the pointer message — WndProc →
/// <see cref="InkController"/> → <see cref="RequestRender"/> → <c>UpdateLayeredWindow</c>. There
/// is no dispatcher hop and no timer batching. Per pointer move only the live stroke's bounding
/// box is touched: the committed layer is cached on the CPU, the previous live box is restored
/// directly into the window's DIB, and the new live box is read back into a reused scratch buffer
/// and blended in place. Empty/hidden scenes retain no CPU or GPU render buffers.
/// </para>
/// </summary>
public sealed class OverlayInkPresenter : IDisposable
{
    private readonly LayeredInkWindow _window;
    private readonly InkController _controller;
    private CanvasDevice _device;

    private CanvasRenderTarget _committedTarget;
    private byte[] _committedPixels;
    private IBuffer _committedPixelsBuffer;   // zero-copy wrapper over _committedPixels
    private bool _committedValid;
    private bool _committedChanged;           // committed cache re-rendered since last frame build
    private Matrix3x2 _lastView;
    private readonly CommittedInkLayer _historyTiles = new();
    private IReadOnlyList<InkStroke> _lastPreviewStrokes;
    private Matrix3x2 _lastPreviewTransform;
    private bool _lastViewManipulation;
    private long _lastDotAnimationVersion;
    private long _lastRecognitionVersion;
    private DotAnimationClock _dotClock;

    private CanvasRenderTarget _liveTarget;
    private int _liveCapacityWidth, _liveCapacityHeight;
    private byte[] _livePixels;
    private IBuffer _livePixelsBuffer;        // zero-copy wrapper over _livePixels

    // The box of the DIB currently holding live pixels; restored from the committed cache
    // before the next live composite. Zero size means the frame equals the committed cache.
    private int _liveX, _liveY, _liveW, _liveH;
    private bool _frameIsHidden;
    private bool _frameDirty;                 // DIB written but not yet pushed to the screen
    private Dictionary<InkStroke, (int Stable, Rect Bounds)> _liveDamage = new();
    private Dictionary<InkStroke, (int Stable, Rect Bounds)> _nextLiveDamage = new();

    private bool _rendering;
    private bool _pending;
    private bool _attached;
    private bool _viewportFrameQueued;
    private long _viewportFrameTime;
    private ScreenInkClip _trackingClip;
    private bool _trackingSuppressed;
    private int _sceneUpdateDepth;
    private readonly HashSet<uint> _excludedPointers = new();
    private readonly ScreenViewTransition _trackingTransition = new();
    private DispatcherQueueTimer _trackingTimer;
    private bool _lastTrackingAnimation;

    public bool IsScreenTrackingAnimating => _trackingTransition.IsActive;
    public Matrix3x2 DisplayedView => _trackingTransition.IsActive ? _trackingTransition.Current : _controller.ViewTransform;

    public void TransitionScreenTrackingView(Matrix3x2 previousView, bool animate,
        double durationMilliseconds = ScreenViewTransition.DurationMilliseconds)
    {
        _trackingTransition.Retarget(previousView, _controller.ViewTransform, Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency,
            animate && _attached && InkVisible && !_trackingSuppressed && !_controller.HasActiveInteraction && _controller.Playback is null,
            durationMilliseconds);
        if (_trackingTransition.IsActive) _trackingTimer?.Start();
        else _trackingTimer?.Stop();
        RequestRender();
    }

    public void FinishScreenTrackingTransition()
    {
        bool active = _trackingTransition.IsActive;
        _trackingTimer?.Stop();
        _trackingTransition.Reset(_controller.ViewTransform);
        if (active) RequestRender();
    }

    private void OnScreenTrackingFrame(DispatcherQueueTimer timer, object args)
    {
        if (_controller.HasActiveInteraction || _controller.Playback is not null || !InkVisible
            || _trackingSuppressed || _trackingTransition.Target != _controller.ViewTransform)
        {
            FinishScreenTrackingTransition();
            return;
        }
        _trackingTransition.Sample(Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency);
        if (!_trackingTransition.IsActive) timer.Stop();
        RequestRender();
    }

    public void BeginSceneUpdate() => _sceneUpdateDepth++;
    public void EndSceneUpdate() { if (_sceneUpdateDepth > 0) _sceneUpdateDepth--; if (_sceneUpdateDepth == 0) RequestRender(); }
    public void SetScreenTrackingClip(ScreenInkClip clip, bool suppressed = false)
    {
        if (_trackingSuppressed == suppressed && _trackingClip?.Viewport == clip?.Viewport
            && (_trackingClip?.Occlusions ?? Array.Empty<Rect>()).SequenceEqual(clip?.Occlusions ?? Array.Empty<Rect>())) return;
        _trackingClip = clip; _trackingSuppressed = suppressed;
        _committedValid = false;
        if (clip is null || suppressed) FinishScreenTrackingTransition();
        RequestRender();
    }
    private long _lastPresentTick;
    private bool MoveFrameDue => Stopwatch.GetElapsedTime(_lastPresentTick).TotalMilliseconds >= 1000d / 240;

    // Latency diagnostics: pointer dwTime (GetTickCount epoch, carried by OverlayPointer.TimeMs)
    // versus the tick at which UpdateLayeredWindow returned.
    private bool _inputDriven;
    private bool _moveInput;
    private uint _lastInputTimeMs;
    private uint _lastInputId;
    private OverlayPointerKind _lastInputKind;
    private double _totalInputToPresentMs;

    public OverlayInkPresenter(LayeredInkWindow window, InkController controller)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
        _controller = controller ?? throw new ArgumentNullException(nameof(controller));

        Scene = new InkSceneRenderer();
    }

    public InkSceneRenderer Scene { get; }

    // The layered window already owns a full-screen DIB. Composite directly into it instead
    // of keeping a second managed frame and copying it back on every pointer update.
    private unsafe Span<byte> Frame => new((void*)_window.PixelBuffer, checked(_window.Width * _window.Height * 4));
    public long ManagedBufferBytes => (_committedPixels?.LongLength ?? 0) + (_livePixels?.LongLength ?? 0);
    public long RenderTargetBytes => (_committedTarget is null ? 0 : (long)_window.Width * _window.Height * 4)
        + (_liveTarget is null ? 0 : (long)_liveCapacityWidth * _liveCapacityHeight * 4) + _historyTiles.RasterBytes;

    /// <summary>Frames actually pushed to the screen; useful for diagnostics.</summary>
    public long PresentedFrames { get; private set; }

    /// <summary>Total bytes read back from the GPU; lets the self-test prove the optimisation.</summary>
    public long GpuReadbackBytes { get; private set; }

    /// <summary>
    /// When set (the orchestrator wires this to the <c>--diag</c> flag), every pointer-driven
    /// present writes its input→present latency to <see cref="FileLog"/>. Counters below are
    /// always maintained so the self-test can read them without enabling logging.
    /// </summary>
    public static bool LatencyLogEnabled { get; set; }

    /// <summary>Number of valid pointer→present latency samples collected.</summary>
    public long LatencySamples { get; private set; }

    /// <summary>Most recent pointer dwTime → UpdateLayeredWindow latency, in milliseconds.</summary>
    public double LastInputToPresentMs { get; private set; }

    /// <summary>Largest pointer→present latency observed, in milliseconds.</summary>
    public double MaxInputToPresentMs { get; private set; }

    /// <summary>Mean pointer→present latency, in milliseconds (0 when no samples).</summary>
    public double AverageInputToPresentMs => LatencySamples == 0 ? 0 : _totalInputToPresentMs / LatencySamples;

    /// <summary>
    /// Cursor/mouse mode keeps the overlay hit-test surface alive but suppresses its visible ink,
    /// matching the WPF CursorIcon_Click path that hid the ink canvas for desktop interaction.
    /// </summary>
    public bool InkVisible
    {
        get => _inkVisible;
        set
        {
            if (_inkVisible == value) return;
            if (value) _window.EnsurePixelBuffer();
            _inkVisible = value;
            FinishScreenTrackingTransition();
            if (!value) _controller.HideEraserPreview();
            RequestRender();
        }
    }
    private bool _inkVisible = true;

    private CanvasRenderTarget CreateTarget(int width, int height) => new(
        _device ??= CanvasDevice.GetSharedDevice(), width, height, 96,
        DirectXPixelFormat.B8G8R8A8UIntNormalized, CanvasAlphaMode.Premultiplied);

    public void Attach()
    {
        if (_attached) return;
        _attached = true;
        _trackingTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _trackingTimer.Interval = TimeSpan.FromMilliseconds(16);
        _trackingTimer.Tick += OnScreenTrackingFrame;
        _dotClock = new DotAnimationClock(_controller, RequestRender);
        _controller.ViewportReleaseMotionEnabled = true;
        _controller.ViewportFrameRequested += ScheduleViewportFrame;
        _window.PointerDown += OnDown;
        _window.PointerMove += OnMove;
        _window.PointerUp += OnUp;
        _window.PointerCanceled += OnCanceled;
        _window.PointerHover += OnHover;
        _window.PointerExited += OnExited;
        _controller.Invalidated += RequestRender;
        _controller.EraserPreviewChanged += RequestRender;
        _controller.Document.Changed += OnDocumentChanged;
        _controller.LayersChanged += OnLayersChanged;
        _controller.SelectionChanged += RequestRender;
        _controller.PlaybackChanged += InvalidateCommitted;
        _controller.ElementsChanged += OnElementsChanged;
        Scene.Images.Changed += InvalidateImageContent;
        Scene.Strokes.Retain(_controller.Document.Strokes);
        RequestRender();
    }

    public void Detach()
    {
        if (!_attached) return;
        _attached = false;
        FinishScreenTrackingTransition();
        _trackingTimer.Tick -= OnScreenTrackingFrame;
        _trackingTimer = null;
        _dotClock?.Dispose();
        _dotClock = null;
        _controller.ViewportFrameRequested -= ScheduleViewportFrame;
        CompositionTarget.Rendering -= RenderViewportFrame;
        _viewportFrameQueued = false;
        _window.PointerDown -= OnDown;
        _window.PointerMove -= OnMove;
        _window.PointerUp -= OnUp;
        _window.PointerCanceled -= OnCanceled;
        _window.PointerHover -= OnHover;
        _window.PointerExited -= OnExited;
        _controller.Invalidated -= RequestRender;
        _controller.EraserPreviewChanged -= RequestRender;
        _controller.Document.Changed -= OnDocumentChanged;
        _controller.LayersChanged -= OnLayersChanged;
        _controller.SelectionChanged -= RequestRender;
        _controller.PlaybackChanged -= InvalidateCommitted;
        _controller.ElementsChanged -= OnElementsChanged;
        Scene.Images.Changed -= InvalidateImageContent;
    }

    private void ScheduleViewportFrame()
    {
        if (_viewportFrameQueued || !_attached) return;
        _viewportFrameQueued = true;
        _viewportFrameTime = Stopwatch.GetTimestamp();
        CompositionTarget.Rendering += RenderViewportFrame;
    }

    private void RenderViewportFrame(object sender, object args)
    {
        CompositionTarget.Rendering -= RenderViewportFrame;
        _viewportFrameQueued = false;
        if (!_attached) return;
        long now = Stopwatch.GetTimestamp();
        float elapsed = (float)Math.Clamp(Stopwatch.GetElapsedTime(_viewportFrameTime, now).TotalSeconds, 0, .05);
        if (_controller.AdvanceViewportFrame(elapsed)) ScheduleViewportFrame();
    }

    private void OnDown(OverlayPointer p)
    {
        if (_trackingSuppressed || _trackingClip?.Contains(new Vector2(p.X, p.Y) * _window.DpiScale) == false)
        { _excludedPointers.Add(p.Id); return; }
        FinishScreenTrackingTransition();
        NoteInput(p); _controller.OnPointerDown(p);
    }
    private void OnMove(OverlayPointer p)
    {
        if (_excludedPointers.Contains(p.Id)) return;
        NoteInput(p); _moveInput = true; _intermediateInput = p.IsIntermediate;
        try { _controller.OnPointerMove(p); }
        finally { _moveInput = false; _intermediateInput = false; }
    }
    private bool _intermediateInput;
    private void OnUp(OverlayPointer p) { if (_excludedPointers.Remove(p.Id)) return; NoteInput(p); _controller.OnPointerUp(p); }
    private void OnCanceled(OverlayPointer p) { if (_excludedPointers.Remove(p.Id)) return; NoteInput(p); _controller.OnPointerCanceled(p); }
    private void OnHover(OverlayPointer p)
    {
        if (_window.ClickThrough || !InkVisible || _trackingSuppressed
            || _trackingClip?.Contains(new Vector2(p.X, p.Y) * _window.DpiScale) == false)
        { _controller.HideEraserPreview(p.Id); return; }
        _moveInput = true;
        try { _controller.OnPointerHover(p); }
        finally { _moveInput = false; }
    }
    private void OnExited(uint pointerId) => _controller.OnPointerExited(pointerId);

    private void NoteInput(OverlayPointer p)
    {
        _inputDriven = true;
        _lastInputTimeMs = p.TimeMs;
        _lastInputId = p.Id;
        _lastInputKind = p.Kind;
    }

    private void OnDocumentChanged()
    {
        Scene.Strokes.Retain(_controller.Document.Strokes);
        InvalidateCommitted();
    }

    private void OnLayersChanged()
    {
        _historyTiles.Invalidate();
        _committedValid = false;
    }

    private void OnElementsChanged()
    {
        Scene.Images.Retain(_controller);
        if (_controller.PreviewStrokes is null) _committedValid = false;
    }

    private void InvalidateImageContent()
    {
        _committedValid = false;
        RequestRender();
    }

    private void InvalidateCommitted()
    {
        _historyTiles.Invalidate();
        _committedValid = false;
        RequestRender();
    }

    /// <summary>
    /// Coalesces invalidations: if a render is already running, mark pending and let it loop
    /// once more rather than recursing through the drawing session. The frame is rebuilt and
    /// then presented once; a re-entrant invalidation during present loops again.
    /// </summary>
    private void RequestRender()
    {
        if (!_attached) return;
        if (_intermediateInput) return;
        if (_sceneUpdateDepth > 0) { _pending = true; return; }
        // Preserve every sample but skip GPU work for intermediate packets already superseded in the queue.
        // Down/up and document changes always flush synchronously.
        if (_moveInput && !MoveFrameDue && _window.HasQueuedPointerMessage()) return;
        if (_rendering) { _pending = true; return; }
        _rendering = true;
        try
        {
            do
            {
                do
                {
                    _pending = false;
                    UpdateFrame();
                } while (_pending);
                PresentFrame();
            } while (_pending);
        }
        finally
        {
            _rendering = false;
            _inputDriven = false;
        }
    }

    /// <summary>Composites into the window's existing DIB; no duplicate full-screen frame.</summary>
    private void UpdateFrame()
    {
        if (_window.PixelBuffer == IntPtr.Zero) return;

        if (!InkVisible || _trackingSuppressed || (_controller.Document.Count == 0 && !ImageSceneRenderer.Images(_controller).Any() && !_controller.LiveStrokes.Any()
            && !_controller.ShapePreview.Any() && _controller.ExtensionPreview.Count == 0 && !_controller.HasSelection && !_controller.IsRegionSelecting && !_controller.IsLassoErasing
            && !_controller.IsEraserPreviewVisible && !_controller.IsPenCursorVisible))
        {
            // Keep alpha at one so the layered window remains hit-testable. The window's
            // WS_EX_TRANSPARENT style then routes desktop pointer input through in cursor mode.
            // The pattern never changes, so build and push it once, not on every invalidation.
            if (!_frameIsHidden)
            {
                Frame.Fill(1);
                _frameIsHidden = true;
                _liveW = 0; _liveH = 0;
                _frameDirty = true;
            }
            ReleaseRenderBuffers();
            return;
        }

        EnsureCommittedCache();

        bool fullRefresh = _committedChanged || _frameIsHidden;
        _frameIsHidden = false;
        if (fullRefresh)
        {
            // Document/selection/view changed: rebuild the whole frame from the committed cache.
            _committedPixels.AsSpan().CopyTo(Frame);
            _liveW = 0; _liveH = 0;
            _committedChanged = false;
            _liveDamage.Clear();
        }
        else if (_liveW > 0 && _liveH > 0)
        {
            RestoreLiveRegion();
        }

        bool layeredLive = InkSceneRenderer.NeedsLayeredLiveInk(_controller);
        bool incremental = !layeredLive && _controller.Playback is null && _controller.PreviewStrokes is null
            && !_controller.ShapePreview.Any() && _controller.RecognitionPreview.Count == 0
            && _controller.ExtensionPreview.Count == 0 && _controller.DotAnimations.Count == 0
            && !_controller.HasSelection && !_controller.IsRegionSelecting && !_controller.IsLassoErasing
            && !_controller.IsEraserPreviewVisible && !_controller.IsPenCursorVisible;
        var region = LiveRegion(incremental);
        if (region.Width > 0 && region.Height > 0)
        {
            RestoreRegion(region.X, region.Y, region.Width, region.Height);
            CompositeLiveRegion(region, layeredLive);
        }
        else ReleaseLiveBuffer();

        // Both restoring the previous live region and blending the current one change the
        // DIB in place; only the final UpdateLayeredWindow upload remains.
        _frameDirty |= fullRefresh || (_liveW > 0 && _liveH > 0) || (region.Width > 0 && region.Height > 0);

        _liveX = region.X; _liveY = region.Y;
        _liveW = incremental ? 0 : region.Width; _liveH = incremental ? 0 : region.Height;
    }

    private void PresentFrame()
    {
        if (!_frameDirty) return;
        // A newer pointer message is already queued: it will present the latest state, so skip
        // this stale full-surface upload rather than presenting twice back-to-back. No latency is
        // added — work is only dropped when the message pump is behind the digitizer.
        if (_moveInput && !MoveFrameDue && _window.HasQueuedPointerMessage()) return;
        _window.Present();
        _lastPresentTick = Stopwatch.GetTimestamp();
        _frameDirty = false;
        PresentedFrames++;
        SampleLatency();
    }

    private void SampleLatency()
    {
        if (!_inputDriven || _lastInputTimeMs == 0) return;
        uint delta = unchecked((uint)Environment.TickCount - _lastInputTimeMs);
        if (delta > 2000) return; // synthetic timestamps or tick wrap: not a real measurement
        LastInputToPresentMs = delta;
        if (delta > MaxInputToPresentMs) MaxInputToPresentMs = delta;
        _totalInputToPresentMs += delta;
        LatencySamples++;
        if (LatencyLogEnabled)
            FileLog.WriteLogToFile(
                $"ink latency: pointer {_lastInputId} {_lastInputKind} dwTime->present {delta} ms",
                LogType.Trace);
    }

    private void EnsureCommittedCache()
    {
        var view = OverlayTransform();
        bool viewChanged = _lastView != view || _lastTrackingAnimation != _trackingTransition.IsActive;
        // During a selection drag this is the frozen restore image. The moving scene is
        // redrawn only inside LiveRegion, including the frozen and current object positions.
        bool previewChanged = !ReferenceEquals(_lastPreviewStrokes, _controller.PreviewStrokes);
        if (_committedValid && !viewChanged && !previewChanged && _lastViewManipulation == _controller.IsViewManipulating
            && _lastDotAnimationVersion == _controller.DotAnimationVersion
            && _lastRecognitionVersion == _controller.RecognitionSourcesVersion) return;

        _committedTarget ??= CreateTarget(_window.Width, _window.Height);
        if (_committedPixels is null)
        {
            _committedPixels = new byte[checked(_window.Width * _window.Height * 4)];
            _committedPixelsBuffer = _committedPixels.AsBuffer();
        }

        using (var session = _committedTarget.CreateDrawingSession())
        {
            // Alpha 1 (premultiplied RGB 1): keep the layered window hit-testable while
            // remaining visually transparent. Stroke coordinates are DIPs; the target buffer
            // is physical pixels, so scale the view transform by the monitor DPI.
            session.Clear(Windows.UI.Color.FromArgb(1, 1, 1, 1));
            _historyTiles.Draw(session, Scene, _controller, _window.Width, _window.Height, transformOverride: view,
                reuseRaster: _trackingTransition.IsActive);
        }
        _committedTarget.GetPixelBytes(_committedPixelsBuffer);
        _trackingClip?.Apply(_committedPixels, _window.Width, _window.Height);
        GpuReadbackBytes += _committedPixels.Length;

        _committedValid = true;
        _committedChanged = true;
        _lastView = view;
        _lastTrackingAnimation = _trackingTransition.IsActive;
        _lastPreviewStrokes = _controller.PreviewStrokes;
        _lastPreviewTransform = _controller.SelectionPreviewTransform;
        _lastViewManipulation = _controller.IsViewManipulating;
        _lastDotAnimationVersion = _controller.DotAnimationVersion;
        _lastRecognitionVersion = _controller.RecognitionSourcesVersion;
    }

    /// <summary>Screen-space bounding box of everything still being drawn.</summary>
    private (int X, int Y, int Width, int Height) LiveRegion(bool incremental)
    {
        var world = Rect.Empty;
        _nextLiveDamage.Clear();
        foreach (var stroke in _controller.LiveStrokes)
        {
            bool previous = _liveDamage.TryGetValue(stroke, out var saved);
            var damage = incremental && previous ? stroke.BoundsFrom(Math.Max(0, saved.Stable - 1)) : stroke.Bounds;
            var bounds = previous ? saved.Bounds.Union(damage) : damage;
            _nextLiveDamage[stroke] = (stroke.StablePointCount, bounds);
            world = world.Union(damage);
        }
        // Touch-to-gesture or pen-priority cancellation must also erase its last visible pixels.
        foreach (var pair in _liveDamage)
            if (!_nextLiveDamage.ContainsKey(pair.Key)) world = world.Union(pair.Value.Bounds);
        (_liveDamage, _nextLiveDamage) = (_nextLiveDamage, _liveDamage);
        foreach (var s in _controller.ShapePreview) world = world.Union(s.Bounds);
        foreach (var s in _controller.RecognitionPreview) world = world.Union(s.Bounds);
        foreach (var s in _controller.ExtensionPreview) world = world.Union(s.Bounds);
        foreach (var animation in _controller.DotAnimations) world = world.Union(animation.Source.Bounds).Union(animation.Preview.Bounds);
        if (_controller.PreviewStrokes is not null)
            world = world.Union(_controller.SelectionPreviewSourceBounds)
                .Union(CommittedInkLayer.TransformBounds(_controller.SelectionPreviewSourceBounds, _lastPreviewTransform))
                .Union(CommittedInkLayer.TransformBounds(_controller.SelectionPreviewSourceBounds, _controller.SelectionPreviewTransform));
        if (_controller.Playback is { } playback)
            foreach (var s in playback.VisibleStrokes) world = world.Union(s.Bounds);
        var adornment = _controller.SelectionVisualBounds.Union(_controller.LassoVisualBounds).Union(_controller.EraserVisualBounds)
            .Union(_controller.PenCursorVisualBounds).Union(_controller.AdvancedSelectionVisualBounds);
        if (!adornment.IsEmpty && Matrix3x2.Invert(_controller.ViewTransform, out var inverse))
        {
            foreach (var p in new[] { new Vector2(adornment.Left, adornment.Top), new Vector2(adornment.Right, adornment.Top),
                new Vector2(adornment.Right, adornment.Bottom), new Vector2(adornment.Left, adornment.Bottom) })
            {
                var w = Vector2.Transform(p, inverse);
                world = world.Union(Rect.FromLTRB(w.X - 1, w.Y - 1, w.X + 1, w.Y + 1));
            }
        }
        if (world.IsEmpty) return (0, 0, 0, 0);

        var view = OverlayTransform();
        var c1 = Vector2.Transform(new Vector2(world.Left, world.Top), view);
        var c2 = Vector2.Transform(new Vector2(world.Right, world.Top), view);
        var c3 = Vector2.Transform(new Vector2(world.Right, world.Bottom), view);
        var c4 = Vector2.Transform(new Vector2(world.Left, world.Bottom), view);
        float minX = MathF.Min(MathF.Min(c1.X, c2.X), MathF.Min(c3.X, c4.X));
        float minY = MathF.Min(MathF.Min(c1.Y, c2.Y), MathF.Min(c3.Y, c4.Y));
        float maxX = MathF.Max(MathF.Max(c1.X, c2.X), MathF.Max(c3.X, c4.X));
        float maxY = MathF.Max(MathF.Max(c1.Y, c2.Y), MathF.Max(c3.Y, c4.Y));

        int x = Math.Max(0, (int)MathF.Floor(minX) - 4);
        int y = Math.Max(0, (int)MathF.Floor(minY) - 4);
        int right = Math.Min(_window.Width, (int)MathF.Ceiling(maxX) + 4);
        int bottom = Math.Min(_window.Height, (int)MathF.Ceiling(maxY) + 4);
        return (x, y, Math.Max(0, right - x), Math.Max(0, bottom - y));
    }

    /// <summary>Copies the previous live box back from the committed cache into the frame.</summary>
    private void RestoreLiveRegion()
        => RestoreRegion(_liveX, _liveY, _liveW, _liveH);

    private void RestoreRegion(int left, int top, int width, int height)
    {
        int stride = _window.Width * 4;
        if (left == 0 && width == _window.Width)
        {
            _committedPixels.AsSpan(top * stride, height * stride).CopyTo(Frame.Slice(top * stride));
            return;
        }
        int rowBytes = width * 4;
        for (int y = 0; y < height; y++)
        {
            int off = (top + y) * stride + left * 4;
            _committedPixels.AsSpan(off, rowBytes).CopyTo(Frame.Slice(off));
        }
    }

    private void CompositeLiveRegion((int X, int Y, int Width, int Height) region, bool layeredLive)
    {
        // Reuse capacity while drawing; allocate only when the live box outgrows it.
        if (_liveTarget is null || region.Width > _liveCapacityWidth || region.Height > _liveCapacityHeight)
        {
            // Geometric growth avoids a new large byte array at every 64px of a long stroke.
            // Bound the scratch surface by the screen and release it after large strokes.
            if (region.Width > _liveCapacityWidth)
                _liveCapacityWidth = Math.Min(_window.Width, Math.Max((region.Width + 63) & ~63,
                    Math.Max(128, _liveCapacityWidth * 2)));
            if (region.Height > _liveCapacityHeight)
                _liveCapacityHeight = Math.Min(_window.Height, Math.Max((region.Height + 63) & ~63,
                    Math.Max(128, _liveCapacityHeight * 2)));
            _liveTarget?.Dispose();
            _liveTarget = CreateTarget(_liveCapacityWidth, _liveCapacityHeight);
            int bytes = _liveCapacityWidth * _liveCapacityHeight * 4;
            if (_livePixels is null || _livePixels.Length < bytes)
            {
                _livePixels = new byte[bytes];
                _livePixelsBuffer = _livePixels.AsBuffer();
            }
        }

        // Render world-DIP geometry into a physical-pixel subregion.
        var transform = OverlayTransform() * Matrix3x2.CreateTranslation(-region.X, -region.Y);

        bool replace = layeredLive || _controller.PreviewStrokes is not null;
        using (var session = _liveTarget.CreateDrawingSession())
        {
            session.Clear(replace ? Windows.UI.Color.FromArgb(1, 1, 1, 1) : Windows.UI.Color.FromArgb(0, 0, 0, 0));
            if (layeredLive)
                Scene.DrawWithLayeredLiveInk(session, _controller, transform, new Rect(-2, -2, region.Width + 2, region.Height + 2));
            else
            {
                if (replace) _historyTiles.Draw(session, Scene, _controller, region.Width, region.Height, transformOverride: transform);
                Scene.DrawLive(session, _controller, transform);
            }
        }

        // Read back only the live box into the reused buffer (no per-frame allocation).
        _liveTarget.GetPixelBytes(_livePixelsBuffer, 0, 0, region.Width, region.Height);
        _trackingClip?.Apply(_livePixels.AsSpan(0, region.Width * region.Height * 4), region.Width, region.Height,
            region.X, region.Y, background: replace ? (byte)1 : (byte)0);
        int liveBytes = region.Width * region.Height * 4;
        GpuReadbackBytes += liveBytes;
        if (replace)
        {
            // Replace the affected scene region, so old ink disappears and translucent
            // selected strokes keep their exact stacking against stationary strokes.
            int rowBytes = region.Width * 4;
            for (int y = 0; y < region.Height; y++)
                _livePixels.AsSpan(y * rowBytes, rowBytes).CopyTo(Frame.Slice(((region.Y + y) * _window.Width + region.X) * 4, rowBytes));
        }
        else PixelCompositing.BlendOver(Frame, _window.Width, _window.Height,
            region.X, region.Y, _livePixels.AsSpan(0, liveBytes), region.Width, region.Height);
    }

    private Matrix3x2 OverlayTransform()
        => DisplayedView * Matrix3x2.CreateScale(_window.DpiScale);

    public void Dispose()
    {
        Detach();
        Scene.Dispose();
        ReleaseRenderBuffers();
    }

    private void ReleaseLiveBuffer()
    {
        // Keep a small scratch target for the next stroke; a large screen-spanning stroke
        // must not leave its peak-sized CPU and GPU buffers resident indefinitely.
        if ((long)_liveCapacityWidth * _liveCapacityHeight <= 256 * 256) return;
        _liveTarget?.Dispose();
        _liveTarget = null;
        _livePixelsBuffer = null;
        _livePixels = null;
        _liveCapacityWidth = _liveCapacityHeight = 0;
    }

    public void ReleaseRenderBuffers()
    {
        _liveDamage.Clear(); _nextLiveDamage.Clear();
        _historyTiles.Dispose();
        Scene.Strokes.RetainLive(Array.Empty<InkStroke>());
        _committedTarget?.Dispose();
        _committedTarget = null;
        _committedPixelsBuffer = null;
        _committedPixels = null;
        _committedValid = false;
        _committedChanged = false;
        _liveTarget?.Dispose();
        _liveTarget = null;
        _livePixelsBuffer = null;
        _livePixels = null;
        _liveCapacityWidth = _liveCapacityHeight = 0;
    }

    public void Suspend()
    {
        _controller.HideEraserPreview();
        Detach();
        ReleaseRenderBuffers();
        Scene.Strokes.InvalidateAll();
        _frameIsHidden = false;
        _frameDirty = false;
        _liveW = _liveH = 0;
    }

    public void ReleaseIdleResources()
    {
        ReleaseRenderBuffers();
        Scene.ReleaseResources();
        _frameIsHidden = false;
        _frameDirty = false;
        _liveW = _liveH = 0;
    }
}
