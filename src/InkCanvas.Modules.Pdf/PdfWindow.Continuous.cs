using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Rect = Windows.Foundation.Rect;

namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    private sealed record PagePreview(CanvasBitmap Bitmap, double Scale);
    private readonly Dictionary<int, PagePreview> _pagePreviews = new();
    private readonly Dictionary<int, InkSceneRenderer> _pageScenes = new();
    private readonly Dictionary<int, Core.Ink.Rect> _pageInkBounds = new();
    private readonly ScrollBar _scrollbar = new() { Orientation = Orientation.Vertical, Width = 20, Minimum = 0, IndicatorMode = ScrollingIndicatorMode.MouseIndicator };
    private readonly ScrollBar _horizontalScrollbar = new() { Orientation = Orientation.Horizontal, Height = 20, Minimum = 0, IndicatorMode = ScrollingIndicatorMode.MouseIndicator };
    private bool _scrollUpdating, _previewQueued, _activePageQueued;
    private int[] _requestedPages = Array.Empty<int>();
    private double _requestedScale;
    public long VisiblePreviewBytes => _pagePreviews.Values.Sum(item => (long)item.Bitmap.SizeInPixels.Width * item.Bitmap.SizeInPixels.Height * 4);
    public int VisiblePageCount => VisiblePages().Length;
    internal Vector2 ViewportSize => new((float)_viewport.ActualWidth, (float)_viewport.ActualHeight);
    internal InkCanvas.Controls.Controls.InkSurface AnnotationSurface => _ink;
    private Matrix3x2 DocumentView => _layout.DocumentView(_pageIndex, Controller.ViewTransform);

    private IReadOnlyList<InkStroke> PageInk(int index)
        => _controllers.TryGetValue(index, out var controller) ? controller.Document.Strokes : _file.GetInk(index);

    private void UpdateInkBounds(int index)
    {
        var bounds = Core.Ink.Rect.Empty;
        foreach (var stroke in PageInk(index)) bounds = bounds.Union(stroke.Bounds);
        if (bounds.IsEmpty) _pageInkBounds.Remove(index);
        else _pageInkBounds[index] = bounds;
    }

    private IEnumerable<int> VisibleInkPages()
    {
        if (!Matrix3x2.Invert(DocumentView, out var inverse)) yield break;
        var first = Vector2.Transform(Vector2.Zero, inverse);
        var last = Vector2.Transform(ViewportSize, inverse);
        var viewport = new Core.Ink.Rect(first.X, first.Y, last.X, last.Y);
        foreach (var pair in _pageInkBounds)
        {
            var origin = new Vector2(_layout[pair.Key].Left, _layout[pair.Key].Top);
            var bounds = pair.Value;
            if (viewport.Intersects(new(bounds.Left + origin.X, bounds.Top + origin.Y, bounds.Right + origin.X, bounds.Bottom + origin.Y)))
                yield return pair.Key;
        }
    }

    private int[] VisiblePages()
    {
        if (Controller is null || !Matrix3x2.Invert(DocumentView, out var inverse)) return Array.Empty<int>();
        float top = Vector2.Transform(Vector2.Zero, inverse).Y;
        float bottom = Vector2.Transform(new(0, (float)_viewport.ActualHeight), inverse).Y;
        return _layout.VisiblePages(top, bottom).ToArray();
    }

    internal void ActivateTouchedPage(OverlayPointer sample)
    {
        if (Controller is null || Controller.HasActiveInteraction || !Matrix3x2.Invert(DocumentView, out var inverse)) return;
        var point = Vector2.Transform(new(sample.X, sample.Y), inverse);
        int index = _layout.NearestPage(point.Y);
        ActivatePage(index, preserveView: true);
    }

    private void QueueActivePage()
    {
        if (_activePageQueued) return;
        _activePageQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { _activePageQueued = false; UpdateActivePage(); });
    }

    private void UpdateActivePage()
    {
        if (_closed || _pageChanging || Controller is null || Controller.HasActiveInteraction) return;
        var before = Controller.ViewTransform;
        ClampDocumentView();
        if (Controller.ViewTransform != before) Controller.NotifyElementsChanged();
        if (!Matrix3x2.Invert(DocumentView, out var inverse)) return;
        float y = Vector2.Transform(new(0, (float)_viewport.ActualHeight / 2), inverse).Y;
        ActivatePage(_layout.NearestPage(y), preserveView: true);
    }

    public void ScrollBy(float delta)
    {
        if (Controller is null || Controller.HasActiveInteraction) return;
        StopScrollMotion();
        _fitMode = FitMode.Free;
        var view = Controller.ViewTransform;
        view.Translation += new Vector2(0, delta);
        Controller.ViewTransform = view;
        ClampDocumentView();
        Controller.NotifyElementsChanged();
        UpdateActivePage();
    }

    private void ClampDocumentView()
    {
        if (!_viewHasLayout) return;
        var document = DocumentView;
        var offset = ScrollBounds.Clamp(document.Translation) - document.Translation;
        if (offset.LengthSquared() > .000001f)
        {
            var view = Controller.ViewTransform;
            view.Translation += offset;
            Controller.ViewTransform = view;
        }
    }

    private void UpdateScrollbar()
    {
        if (_scrollUpdating || !_viewHasLayout) return;
        _scrollUpdating = true;
        try
        {
            var document = DocumentView;
            var bounds = ScrollBounds;
            var offset = bounds.Offset(document.Translation);
            _scrollbar.ViewportSize = _viewport.ActualHeight;
            _scrollbar.Maximum = bounds.Extent.Y;
            _scrollbar.LargeChange = Math.Max(32, _viewport.ActualHeight * .85);
            _scrollbar.SmallChange = 70;
            _scrollbar.IsEnabled = bounds.Extent.Y > .01f;
            _scrollbar.Value = offset.Y;
            _horizontalScrollbar.ViewportSize = _viewport.ActualWidth;
            _horizontalScrollbar.Maximum = bounds.Extent.X;
            _horizontalScrollbar.LargeChange = Math.Max(32, _viewport.ActualWidth * .85);
            _horizontalScrollbar.SmallChange = 70;
            _horizontalScrollbar.IsEnabled = bounds.Extent.X > .01f;
            _horizontalScrollbar.Value = offset.X;
        }
        finally { _scrollUpdating = false; }
    }

    private void ScrollbarChanged()
    {
        if (_scrollUpdating || Controller is null) return;
        if (Controller.HasActiveInteraction) { UpdateScrollbar(); return; }
        StopScrollMotion();
        _fitMode = FitMode.Free;
        var view = Controller.ViewTransform;
        view.Translation += ScrollBounds.Translation(new((float)_horizontalScrollbar.Value, (float)_scrollbar.Value)) - DocumentView.Translation;
        Controller.ViewTransform = view;
        Controller.NotifyElementsChanged();
        UpdateActivePage();
    }

    private void QueuePageRendering()
    {
        if (_closed || !_idleResources.IsActive || !_viewHasLayout || _previewQueued) return;
        _previewQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _previewQueued = false;
            if (!_closed) _ = RenderPageAsync();
        });
    }

    private async Task RenderPageAsync()
    {
        if (_closed || !_idleResources.IsActive || !_viewHasLayout) return;
        var visible = VisiblePages();
        if (visible.Length == 0) return;
        double area = visible.Sum(index => (double)_file.Pages[index].DipWidth * _file.Pages[index].DipHeight);
        double zoom = Math.Sqrt(Math.Abs(DocumentView.GetDeterminant())) * (_root.XamlRoot?.RasterizationScale ?? 1);
        // The full visible set shares a 24-megapixel budget even when zoomed far out.
        double scale = Math.Min(1.4, Math.Min(zoom, Math.Sqrt(24_000_000 / Math.Max(1, area))));
        scale = 1.4 / Math.Pow(2, Math.Max(0, Math.Ceiling(Math.Log2(1.4 / Math.Max(.01, scale)))));
        bool Ready(int index) => _pagePreviews.TryGetValue(index, out var preview) && preview.Scale >= scale * .99 && preview.Scale <= scale * 2.1;
        if (visible.All(Ready))
        {
            if (_renderPending && !visible.SequenceEqual(_requestedPages)) _renderCancellation?.Cancel();
            TrimPagePreviews(visible);
            return;
        }
        if (_renderPending && visible.SequenceEqual(_requestedPages) && Math.Abs(scale - _requestedScale) < .001) return;
        _requestedPages = visible; _requestedScale = scale;
        _renderCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _renderCancellation = cancellation;
        _renderPending = true;
        try
        {
            foreach (int index in visible.OrderBy(index => Math.Abs(index - _pageIndex)))
            {
                if (Ready(index)) continue;
                cancellation.Token.ThrowIfCancellationRequested();
                var page = _file.Pages[index];
                var bytes = await _rasterizer.RenderAsync(index, page, new Rect(0, 0, page.DipWidth, page.DipHeight), scale, cancellation.Token);
                var bitmap = await LoadBitmapAsync(bytes, cancellation.Token);
                if (_closed || cancellation.IsCancellationRequested) { bitmap.Dispose(); return; }
                if (_pagePreviews.Remove(index, out var previous)) previous.Bitmap.Dispose();
                _pagePreviews[index] = new(bitmap, scale);
                TrimPagePreviews(visible);
                _paper.Invalidate();
            }
            _detailTimer.Stop(); _detailTimer.Start();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && !cancellation.IsCancellationRequested) SetStatus("页面渲染失败 · " + ex.Message); }
        finally
        {
            if (ReferenceEquals(_renderCancellation, cancellation)) { _renderPending = false; _renderCancellation = null; }
            cancellation.Dispose();
        }
    }

    private void TrimPagePreviews(int[] visible)
    {
        foreach (int index in _pagePreviews.Keys.Where(index => !visible.Contains(index)).OrderByDescending(index => Math.Abs(index - _pageIndex)).ToArray())
        {
            if (_pagePreviews.Count <= Math.Max(6, visible.Length) && VisiblePreviewBytes <= 104 * 1024 * 1024) break;
            _pagePreviews[index].Bitmap.Dispose(); _pagePreviews.Remove(index);
        }
        var inkPages = VisibleInkPages().ToHashSet();
        foreach (int index in _pageScenes.Keys.Where(index => !inkPages.Contains(index)).ToArray())
        { _pageScenes[index].Dispose(); _pageScenes.Remove(index); }
    }

    private void ReleasePagePreviews()
    {
        foreach (var preview in _pagePreviews.Values) preview.Bitmap.Dispose();
        _pagePreviews.Clear();
        foreach (var scene in _pageScenes.Values) scene.Dispose();
        _pageScenes.Clear();
    }

    private void DrawPage(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_closed || _idleResources.IsReleased || Controller is null) return;
        var draw = args.DrawingSession;
        var document = DocumentView;
        foreach (int index in VisiblePages())
        {
            var page = _file.Pages[index];
            var view = _layout.PageView(index, document);
            var bounds = new Rect(0, 0, page.DipWidth, page.DipHeight);
            draw.Transform = view;
            float border = 1 / MathF.Sqrt(MathF.Abs(view.GetDeterminant()));
            draw.DrawRectangle(new Rect(-border, -border, bounds.Width + 2 * border, bounds.Height + 2 * border), _pageEdgeColor, border);
            draw.FillRectangle(bounds, Microsoft.UI.Colors.White);
            if (_pagePreviews.TryGetValue(index, out var preview)) draw.DrawImage(preview.Bitmap, bounds, preview.Bitmap.Bounds);
            if (index == _pageIndex && _detail is not null) draw.DrawImage(_detail, _detailRect, _detail.Bounds);
            DrawSearchHighlights(draw, index);
        }
        draw.Transform = Matrix3x2.Identity;
        // All page backgrounds precede ink so margin strokes can cross page gaps.
        foreach (int index in VisibleInkPages())
        {
            if (index == _pageIndex) continue;
            var view = _layout.PageView(index, document);
            var strokes = PageInk(index);
            if (strokes.Count == 0) continue;
            if (!_pageScenes.TryGetValue(index, out var scene)) _pageScenes[index] = scene = new InkSceneRenderer { ShowSelectionOutline = false };
            scene.Strokes.Retain(strokes);
            foreach (var stroke in strokes) scene.Strokes.Draw(draw, stroke, view);
        }
        draw.Transform = Matrix3x2.Identity;
    }
}
