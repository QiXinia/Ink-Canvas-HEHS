using System.Numerics;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.History;
using InkCanvas.Core.Ink;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Pdf;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Rect = Windows.Foundation.Rect;

namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    private enum SpacingTool { None, Insert, Guide }
    private sealed record SpacingDrag(int Page, Vector2 Start, uint Pointer, Guid BandId);
    private readonly Dictionary<int, PdfPageSpacing> _pageSpacing = new();
    private readonly Dictionary<int, PdfSpacingLane[]> _spacingLanes = new();
    private readonly Dictionary<int, PdfPageGeometry> _displayGeometry = new();
    private readonly Grid _spacingInput = new()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent), Visibility = Visibility.Collapsed,
    };
    private readonly TouchIconButton _deleteSpacing = new()
    {
        Glyph = Glyphs.Delete, Label = "删除", Width = 76, Height = 44, Visibility = Visibility.Collapsed,
    };
    private ToggleMenuFlyoutItem _insertSpacingItem, _guideItem;
    private TouchIconButton _spacingButton;
    private SpacingTool _spacingTool;
    private SpacingDrag _spacingDrag;
    private Vector2 _spacingEnd;
    private PdfPageSpacing _spacingPreview;
    private (int Page, Guid Id)? _layoutSelection;

    private PdfPageSpacing PageSpacing(int page) => _pageSpacing.GetValueOrDefault(page) ?? PdfPageSpacing.Empty;
    private PdfPageGeometry DisplayGeometry(int page) => _displayGeometry.GetValueOrDefault(page) ?? _file.Pages[page];

    private PdfSpacingLane[] SpacingLanes(int page)
    {
        if (!_spacingLanes.TryGetValue(page, out var lanes))
            _spacingLanes[page] = lanes = PageSpacing(page).Lanes(_file.Pages[page]);
        return lanes;
    }

    private void InitializeSpacing()
    {
        for (int index = 0; index < _file.Pages.Count; index++)
        {
            var spacing = _file.GetSpacing(index);
            if (!spacing.IsEmpty)
            {
                _pageSpacing[index] = spacing;
                _displayGeometry[index] = spacing.Expand(_file.Pages[index]);
            }
            _pageRotations[index] = _file.GetDisplayRotation(index);
        }
        _layout = CreatePageLayout();
    }

    private PdfContinuousLayout CreatePageLayout() => new(
        _file.Pages.Select((_, index) => DisplayGeometry(index)).ToArray(), doublePages: _doublePages,
        separateCover: _separateCover, rotations: _pageRotations, pageOrder: _pageOrder);

    private void ConfigureSpacingInput()
    {
        _viewport.Children.Insert(_viewport.Children.IndexOf(_selectionHost), _spacingInput);
        _selectionHost.Children.Add(_deleteSpacing);
        AutomationProperties.SetName(_deleteSpacing, "删除选中的纵向空白或分栏辅助线");
        _deleteSpacing.Click += (_, _) => TryDeleteLayoutSelection();
        _ink.InterceptPointerPressed = TrySelectSpacing;
        _spacingInput.PointerPressed += (_, args) =>
        {
            var sample = args.GetCurrentPoint(_spacingInput);
            if (_spacingDrag is not null || Controller.HasActiveInteraction
                || (sample.PointerDeviceType == Microsoft.UI.Input.PointerDeviceType.Mouse ? !sample.Properties.IsLeftButtonPressed : !sample.IsInContact)
                || !TryPagePoint(sample.Position, out int page, out var point)) return;
            if (_pageRotations[page] != 0)
            {
                SetStatus("请先将此页面旋转回原方向，再编辑空白或分栏辅助线");
                return;
            }
            var geometry = _file.Pages[page];
            float zoom = MathF.Sqrt(MathF.Abs(DocumentView.GetDeterminant()));
            if (point.X < 0 || point.X > geometry.DipWidth || point.Y < 0 || point.Y > DisplayGeometry(page).DipHeight + 14 / zoom) return;
            StopScrollMotion();
            ReleaseNavigationPicture();
            ActivatePage(page, preserveView: true);
            _ink.Focus(FocusState.Programmatic);
            if (!_spacingInput.CapturePointer(args.Pointer)) return;
            _spacingDrag = new(page, point, args.Pointer.PointerId, Guid.NewGuid());
            _spacingEnd = point;
            InputBegan?.Invoke();
            args.Handled = true;
        };
        _spacingInput.PointerMoved += (_, args) =>
        {
            if (_spacingDrag is not { } drag || drag.Pointer != args.Pointer.PointerId) return;
            UpdateSpacingDrag(drag, args.GetCurrentPoint(_spacingInput).Position);
            args.Handled = true;
        };
        _spacingInput.PointerReleased += (_, args) =>
        {
            if (_spacingDrag is not { } drag || drag.Pointer != args.Pointer.PointerId) return;
            UpdateSpacingDrag(drag, args.GetCurrentPoint(_spacingInput).Position);
            var next = _spacingPreview;
            if (_spacingTool == SpacingTool.Guide && Math.Abs(_spacingEnd.Y - drag.Start.Y) >= 8)
                next = AddColumnGuide(drag.Page, drag.Start.X);
            CancelSpacingDrag();
            if (next is not null) ChangeSpacing(drag.Page, next, _spacingTool == SpacingTool.Insert ? "插入纵向空白" : "添加分栏辅助线");
            args.Handled = true;
        };
        _spacingInput.PointerCanceled += (_, _) => CancelSpacingDrag();
        _spacingInput.PointerCaptureLost += (_, _) => CancelSpacingDrag();
        _spacingInput.PointerWheelChanged += Wheel;
    }

    private bool TryPagePoint(Windows.Foundation.Point position, out int page, out Vector2 local)
    {
        page = 0; local = default;
        if (!Matrix3x2.Invert(DocumentView, out var inverse)) return false;
        var point = Vector2.Transform(new((float)position.X, (float)position.Y), inverse);
        page = _layout.NearestPage(point);
        if (_spacingTool == SpacingTool.Insert)
        {
            float tolerance = 12 / MathF.Sqrt(MathF.Abs(DocumentView.GetDeterminant()));
            foreach (int candidate in _layout.VisiblePages(point.Y - tolerance - .01f, point.Y))
            {
                var bounds = _layout[candidate];
                if (_pageRotations[candidate] == 0 && point.X >= bounds.Left && point.X <= bounds.Right
                    && point.Y >= bounds.Bottom && point.Y - bounds.Bottom <= tolerance)
                { page = candidate; break; }
            }
        }
        if (!Matrix3x2.Invert(_layout.PageView(page, Matrix3x2.Identity), out var pageInverse)) return false;
        local = Vector2.Transform(point, pageInverse);
        return true;
    }

    private void SetSpacingTool(SpacingTool tool)
    {
        if (!CanEdit || Controller is null || Controller.HasActiveInteraction) return;
        CancelSpacingDrag();
        ClearSpacingSelection();
        if (tool != SpacingTool.None)
        {
            Controller.ClearSelection();
            Controller.EndPlayback();
            Controller.Tool = InkTool.Pan;
        }
        _spacingTool = tool;
        _spacingInput.Visibility = tool == SpacingTool.None ? Visibility.Collapsed : Visibility.Visible;
        _insertSpacingItem.IsChecked = tool == SpacingTool.Insert;
        _guideItem.IsChecked = tool == SpacingTool.Guide;
        _spacingButton.IsSelected = tool != SpacingTool.None;
        if (tool != SpacingTool.None) SetStatus(tool == SpacingTool.Insert
            ? "从插入位置向下拖动，也可从页面底边拖动" : "沿所需分栏位置拖出竖直辅助线");
        InvalidateSpacing();
    }

    private void UpdateSpacingDrag(SpacingDrag drag, Windows.Foundation.Point position)
    {
        if (!Matrix3x2.Invert(_layout.PageView(drag.Page, DocumentView), out var inverse)) return;
        _spacingEnd = Vector2.Transform(new((float)position.X, (float)position.Y), inverse);
        _spacingPreview = _spacingTool == SpacingTool.Insert ? AddBlankBand(drag.Page, drag.Start, _spacingEnd.Y - drag.Start.Y, drag.BandId) : null;
        bool drawCommitted = _spacingPreview is null || drag.Page != _pageIndex;
        if (_ink.DrawCommitted != drawCommitted)
        {
            _ink.DrawCommitted = drawCommitted;
            Controller.NotifyElementsChanged();
        }
        _paper.Invalidate();
    }

    private void CancelSpacingDrag()
    {
        if (_spacingDrag is null) return;
        _spacingDrag = null; _spacingPreview = null;
        _spacingInput.ReleasePointerCaptures();
        _ink.DrawCommitted = true;
        Controller?.NotifyElementsChanged();
        _paper.Invalidate();
    }

    internal PdfPageSpacing AddBlankBand(int page, Vector2 start, float height, Guid id)
    {
        var spacing = PageSpacing(page);
        if (!float.IsFinite(height) || height < 4 || spacing.Bands.Length >= PdfPageSpacing.MaximumBands) return null;
        var lane = SpacingLanes(page).FirstOrDefault(lane => start.X < lane.Right) ?? SpacingLanes(page)[^1];
        float room = PdfPageSpacing.MaximumAddedHeight - lane.AddedHeight;
        if (room < 4) return null;
        float bottom = _file.Pages[page].DipHeight;
        float zoom = MathF.Sqrt(MathF.Abs(DocumentView.GetDeterminant()));
        float source = Math.Abs(start.Y - (bottom + lane.AddedHeight)) <= 7 / zoom ? bottom
            : Math.Clamp(lane.SourceY(start.Y), 0, bottom);
        if (lane.PlacedBands().Any(item => start.Y >= item.Top && start.Y < item.Top + item.Band.Height)) return null;
        var band = new PdfBlankBand(id, lane.LeftGuide, lane.RightGuide, source, Math.Min(height, room));
        return spacing with { Bands = [..spacing.Bands, band] };
    }

    private PdfPageSpacing AddColumnGuide(int page, float x)
    {
        var spacing = PageSpacing(page);
        if (spacing.Guides.Length >= PdfPageSpacing.MaximumGuides || x < 12 || x > _file.Pages[page].DipWidth - 12
            || spacing.Guides.Any(guide => Math.Abs(guide.X - x) < 12)) return null;
        return spacing with { Guides = [..spacing.Guides, new(Guid.NewGuid(), x)] };
    }

    internal bool TrySelectSpacing(OverlayPointer sample)
    {
        if (Controller.Tool != InkTool.Select || Controller.HasActiveInteraction || !CanEdit) return false;
        if (!TryPagePoint(new(sample.X, sample.Y), out int page, out var point)) return false;
        var spacing = PageSpacing(page);
        if (spacing.IsEmpty) { ClearSpacingSelection(); return false; }
        float tolerance = 7 / MathF.Sqrt(MathF.Abs(DocumentView.GetDeterminant()));
        bool onPage = point.Y >= 0 && point.Y <= DisplayGeometry(page).DipHeight;
        var guide = onPage ? spacing.Guides.OrderBy(item => Math.Abs(item.X - point.X))
            .FirstOrDefault(item => Math.Abs(item.X - point.X) <= tolerance) : null;
        Guid? hit = guide?.Id;
        if (hit is null && point.X >= 0 && point.X <= _file.Pages[page].DipWidth)
        {
            // Ink inside an inserted area remains individually selectable.
            if (PageInk(page).Any(stroke => stroke.InkLayerIndex == Controller.ActiveInkLayer && stroke.Bounds.Inflate(tolerance).Contains(point)
                && stroke.DistanceTo(point) <= tolerance + stroke.HitRadius)) { ClearSpacingSelection(); return false; }
            var lane = SpacingLanes(page).FirstOrDefault(item => point.X < item.Right) ?? SpacingLanes(page)[^1];
            hit = lane.PlacedBands().FirstOrDefault(item => point.Y >= item.Top && point.Y < item.Top + item.Band.Height).Band?.Id;
        }
        if (hit is null) { ClearSpacingSelection(); return false; }
        StopScrollMotion();
        ActivatePage(page, preserveView: true);
        Controller.ClearSelection();
        _layoutSelection = (page, hit.Value);
        Canvas.SetLeft(_deleteSpacing, Math.Clamp(sample.X - 38, 0, Math.Max(0, _viewport.ActualWidth - 76)));
        Canvas.SetTop(_deleteSpacing, Math.Clamp(sample.Y - 50, 0, Math.Max(0, _viewport.ActualHeight - 44)));
        _deleteSpacing.Visibility = Visibility.Visible;
        InvalidateSpacing();
        return true;
    }

    private void ClearSpacingSelection()
    {
        if (_layoutSelection is null) return;
        _layoutSelection = null;
        _deleteSpacing.Visibility = Visibility.Collapsed;
        InvalidateSpacing();
    }

    internal bool TryDeleteLayoutSelection()
    {
        if (_layoutSelection is not { } selected || !CanEdit || Controller.HasActiveInteraction) return false;
        ChangeSpacing(selected.Page, PageSpacing(selected.Page).Remove(selected.Id), "删除页面空白或辅助线");
        return true;
    }

    public void ClearCanvas(bool inkOnly)
    {
        if (!CanEdit || Controller.HasActiveInteraction || _spacingDrag is not null) return;
        if (inkOnly) Controller.ClearHandwriting();
        else if (PageSpacing(_pageIndex).IsEmpty) Controller.Clear();
        else ChangeSpacing(_pageIndex, PdfPageSpacing.Empty, "清屏", clearAll: true);
    }

    internal void ChangeSpacing(int page, PdfPageSpacing next, string name, bool clearAll = false)
    {
        var before = PageSpacing(page);
        if (!CanEdit || !next.IsValid(_file.Pages[page]) || !clearAll && before == next) return;
        NormalizeCrossPageInk();
        var controller = PageController(page);
        var original = controller.Document.Strokes.ToArray();
        if (clearAll && original.Length == 0 && before.IsEmpty) return;
        var updated = clearAll ? Array.Empty<InkStroke>() : PdfPageSpacing.RemapInk(_file.Pages[page], before, next, original);
        Apply(next, updated);
        _inkHistory.Push(new DelegateAction(name, () => Apply(next, updated), () => Apply(before, original)));

        void Apply(PdfPageSpacing spacing, IReadOnlyList<InkStroke> ink)
        {
            var view = Controller.ViewTransform;
            bool previousRouting = _routingInk;
            _routingInk = true;
            try
            {
                ClearSpacingSelection();
                controller.ClearSelection();
                if (spacing.IsEmpty) { _pageSpacing.Remove(page); _displayGeometry.Remove(page); }
                else { _pageSpacing[page] = spacing; _displayGeometry[page] = spacing.Expand(_file.Pages[page]); }
                _spacingLanes.Remove(page);
                _layout = CreatePageLayout();
                _fitMode = FitMode.Free;
                Controller.ViewTransform = view;
                _routedGeometry.Remove(page);
                _dirtyInkPages.Remove(page);
                controller.Document.ReplaceAll(ink);
                _generation++;
                _detailCancellation?.Cancel();
                ClampDocumentView();
                Controller.NotifyElementsChanged();
                InvalidateSpacing();
                UpdateScrollbar();
                UpdateThumbnailGeometry(page);
            }
            finally { _routingInk = previousRouting; }
        }
    }

    private void InvalidateSpacing()
    {
        _navigationPictureDirty = true;
        ReleaseNavigationPicture();
        _paper.Invalidate();
    }

    private (int Page, float RowBottom, float Change) SpacingPreviewLayout()
    {
        if (_spacingDrag is not { } drag || _spacingPreview is null) return (-1, float.MaxValue, 0);
        var geometry = _file.Pages[drag.Page];
        var row = _layout.SpreadBounds(drag.Page);
        float height = _spacingPreview.Expand(geometry).DipHeight;
        return (drag.Page, row.Bottom, MathF.Max(0, _layout[drag.Page].Top + height - row.Bottom));
    }

    internal static void DrawSpacingBitmap(CanvasDrawingSession draw, CanvasBitmap bitmap, Rect bitmapExtent,
        PdfPageGeometry sourcePage, IReadOnlyList<PdfSpacingLane> lanes)
    {
        foreach (var lane in lanes)
        foreach (var (source, shift) in lane.ContentSlices(sourcePage.DipHeight))
        {
            double left = Math.Max(source.Left, bitmapExtent.Left), top = Math.Max(source.Top, bitmapExtent.Top);
            double right = Math.Min(source.Right, bitmapExtent.Right), bottom = Math.Min(source.Bottom, bitmapExtent.Bottom);
            if (right <= left || bottom <= top) continue;
            double sx = bitmap.Bounds.Width / bitmapExtent.Width, sy = bitmap.Bounds.Height / bitmapExtent.Height;
            draw.DrawImage(bitmap, new Rect(left, top + shift, right - left, bottom - top),
                new Rect((left - bitmapExtent.Left) * sx, (top - bitmapExtent.Top) * sy, (right - left) * sx, (bottom - top) * sy));
        }
    }

    private Core.Ink.Rect DetailSourceBounds(int page, Core.Ink.Rect visible)
    {
        var geometry = _file.Pages[page];
        if (PageSpacing(page).Bands.Length == 0) return visible;
        var result = Core.Ink.Rect.Empty;
        foreach (var lane in SpacingLanes(page))
        foreach (var (source, shift) in lane.ContentSlices(geometry.DipHeight))
        {
            var intersection = new Core.Ink.Rect(Math.Max(source.Left, visible.Left), Math.Max(source.Top, visible.Top - shift),
                Math.Min(source.Right, visible.Right), Math.Min(source.Bottom, visible.Bottom - shift));
            if (!intersection.IsEmpty) result = result.Union(intersection);
        }
        return result;
    }

    private void DrawSpacingPreviewInk(CanvasDrawingSession draw, int page, Matrix3x2 view)
    {
        var band = _spacingPreview.Bands[^1];
        var lane = SpacingLanes(page).Single(item => item.LeftGuide == band.LeftGuide && item.RightGuide == band.RightGuide);
        float y = band.SourceY + lane.OffsetAt(band.SourceY);
        var strokes = PageInk(page);
        if (strokes.Count == 0) return;
        if (!_pageScenes.TryGetValue(page, out var scene)) _pageScenes[page] = scene = new InkSceneRenderer { ShowSelectionOutline = false };
        scene.Strokes.Retain(strokes);
        var extent = _pageInkBounds.GetValueOrDefault(page);
        var sorted = InkStroke.InDrawingOrder(strokes).ToArray();
        float left = extent.Left - 1, top = extent.Top - 1, right = extent.Right + 1, bottom = extent.Bottom + 1;
        float start = lane.LeftGuide is null ? left : lane.Left, end = lane.RightGuide is null ? right : lane.Right;
        Draw(new(left, top, start, bottom), 0);
        Draw(new(end, top, right, bottom), 0);
        Draw(new(start, top, end, y), 0);
        Draw(new(start, y, end, bottom), band.Height);

        void Draw(Core.Ink.Rect area, float shift)
        {
            if (area.IsEmpty) return;
            var transform = Matrix3x2.CreateTranslation(0, shift) * view;
            draw.Transform = transform;
            using var clip = draw.CreateLayer(1, new Rect(area.Left, area.Top, area.Width, area.Height));
            foreach (var stroke in sorted)
                if (stroke.Bounds.Intersects(area)) scene.Strokes.Draw(draw, stroke, transform);
        }
        draw.Transform = Matrix3x2.Identity;
    }

    private void DrawSpacingMarks(CanvasDrawingSession draw, int page, float deviceWidth, IReadOnlyList<PdfSpacingLane> lanes)
    {
        bool editing = _spacingTool != SpacingTool.None || Controller.Tool == InkTool.Select;
        if (!editing) return;
        var blue = Windows.UI.Color.FromArgb(220, 57, 108, 216);
        var tint = Windows.UI.Color.FromArgb(30, 57, 108, 216);
        var selected = _layoutSelection?.Page == page ? _layoutSelection.Value.Id : Guid.Empty;
        var spacing = PageSpacing(page);
        float bottom = DisplayGeometry(page).DipHeight;
        foreach (var guide in spacing.Guides)
            draw.DrawLine(guide.X, 0, guide.X, bottom, blue, deviceWidth * (selected == guide.Id ? 3 : 1));
        if (_spacingDrag?.Page == page && _spacingTool == SpacingTool.Guide)
            draw.DrawLine(_spacingDrag.Start.X, 0, _spacingDrag.Start.X, bottom, blue, deviceWidth * 2);
        foreach (var lane in lanes)
        foreach (var (band, top) in lane.PlacedBands())
        {
            var area = new Rect(lane.Left, top, lane.Right - lane.Left, band.Height);
            draw.FillRectangle(area, tint);
            draw.DrawRectangle(area, blue, deviceWidth * (selected == band.Id ? 2 : 1));
        }
    }
}
