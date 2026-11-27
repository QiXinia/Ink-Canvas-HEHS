using System.Numerics;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Lifecycle;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using InkCanvas.Host.Contracts;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Windowing;
using InkCanvas.Pdf;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Streams;
using Rect = Windows.Foundation.Rect;

namespace InkCanvas.Modules.Pdf;

/// <summary>Continuous PDF workspace with page-local ink and bounded visible-page rendering.</summary>
public sealed partial class PdfWindow : Window, IDocumentWorkspace
{
    private readonly IDocumentWorkspaceHost _host;
    private readonly PdfAnnotationFile _file;
    private readonly PdfRasterizer _rasterizer;
    private readonly Dictionary<int, InkController> _controllers = new();
    private readonly Grid _root = new(), _viewport = new(), _commandHost = new();
    private readonly SplitView _split = new() { DisplayMode = SplitViewDisplayMode.Inline, IsPaneOpen = true, OpenPaneLength = 216 };
    private readonly Grid _sidebar = new();
    private readonly List<TouchIconButton> _commandButtons = new();
    private readonly CanvasControl _paper = new() { IsHitTestVisible = false, ClearColor = Microsoft.UI.Colors.Transparent };
    private readonly InkSurface _ink = new();
    private readonly Canvas _selectionHost = new() { IsHitTestVisible = true };
    private readonly Border _selectionBorder = new() { Padding = new Thickness(4), CornerRadius = new CornerRadius(8),
        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(242, 250, 250, 250)), Visibility = Visibility.Collapsed };
    private readonly SelectionToolbar _selection;
    private readonly TextBox _pageNumber = new() { Text = "1", Width = 44, MinWidth = 44, MinHeight = 32,
        Padding = new Thickness(4, 5, 4, 5), TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        InputScope = new InputScope { Names = { new InputScopeName(InputScopeNameValue.Number) } } };
    private readonly TextBlock _pageCount = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2, 0, 4, 0), FontSize = 12 };
    private readonly DropDownButton _zoom = new() { Content = "100%", MinWidth = 76, MinHeight = 40,
        Padding = new Thickness(6, 4, 6, 4), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
    private readonly ListView _pages = new() { SelectionMode = ListViewSelectionMode.Single };
    private readonly ListView _searchResults = new() { SelectionMode = ListViewSelectionMode.Single, Visibility = Visibility.Collapsed };
    private readonly ListView _bookmarks = new() { SelectionMode = ListViewSelectionMode.Single, Visibility = Visibility.Collapsed };
    private readonly AutoSuggestBox _search = new() { PlaceholderText = "搜索文档文字", QueryIcon = new SymbolIcon(Symbol.Find), Margin = new Thickness(8) };
    private readonly DispatcherQueueTimer _detailTimer;
    private readonly WindowIdleResources _idleResources;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource _renderCancellation, _detailCancellation, _searchCancellation;
    private CanvasBitmap _detail;
    private Rect _detailRect;
    private double _detailScale;
    private int _pageIndex, _generation, _version, _savedVersion;
    private PdfContinuousLayout _layout;
    private Matrix3x2 _lastView;
    private bool _closed, _allowClose, _closing, _saving, _pageChanging, _renderPending, _selectionPending, _viewHasLayout;
    private bool _fittingView;
    private string _targetPath;
    private FitMode _fitMode = FitMode.Page;
    private enum FitMode { Free, Page, Width }

    public Window Window => this;
    public InkController Controller { get; private set; }
    public Core.Settings.GestureSettings Gestures { get; } = new() { IsEnableMultiTouchMode = false };
    public bool SupportsViewRotation => false;
    public bool CanEdit => !_file.ReadOnly;
    public event Action ControllerChanged;
    public event Action InputBegan;
    public int PageIndex => _layout.PositionOf(_pageIndex);
    public bool IsDirty => _version != _savedVersion;
    public long PreviewCacheBytes => _rasterizer.CachedBytes;
    public string FilePath => _targetPath;
    public Task Cleanup { get; private set; } = Task.CompletedTask;

    internal PdfWindow(IDocumentWorkspaceHost host, PdfAnnotationFile file, PdfRasterizer rasterizer,
        Func<Task> open, Func<Task> import)
    {
        _host = host; _file = file; _rasterizer = rasterizer; _targetPath = file.SourcePath;
        _ink.ViewportReleaseMotionEnabled = false;
        _paper.CustomDevice = rasterizer.Device;
        _pageRotations = new int[file.Pages.Count];
        _pageOrder = Enumerable.Range(0, file.Pages.Count).ToArray();
        InitializeSpacing();
        _inkHistory.Changed += QueueInkRouting;
        foreach (int index in file.InkPages) UpdateInkBounds(index);
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        ConfigureToolbar(open, import);
        Grid.SetRow(_split, 1); _root.Children.Add(_split);
        ConfigureSidebar();
        ConfigurePageOrganizer();
        _pages.SelectedIndex = 0;
        RefreshBookmarks();
        _bookmarks.SelectionChanged += (_, _) => { if (!_pageChanging && _bookmarks.SelectedItem is BookmarkItem item) OpenSourcePage(item.Bookmark.PageIndex); };
        _split.Pane = _sidebar;
        var pageHost = new Grid();
        pageHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pageHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        pageHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        pageHost.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        pageHost.Children.Add(_viewport);
        Grid.SetColumn(_scrollbar, 1); pageHost.Children.Add(_scrollbar);
        Grid.SetRow(_horizontalScrollbar, 1); pageHost.Children.Add(_horizontalScrollbar);
        _split.Content = pageHost;
        _scrollbar.ValueChanged += (_, _) => ScrollbarChanged();
        _horizontalScrollbar.ValueChanged += (_, _) => ScrollbarChanged();
        _viewport.Children.Add(_paper); _viewport.Children.Add(_ink); _viewport.Children.Add(_navigationPicture); _viewport.Children.Add(_selectionHost);
        ConfigureSpacingInput();
        // A Canvas does not consume input on its empty area. Only the compact toolbar is hit-testable.
        _selection = new SelectionToolbar(() => Controller, async () => await SaveAsync(true), () => _ = import(), "导入画板");
        _selectionBorder.Child = _selection; _selectionHost.Children.Add(_selectionBorder);
        _selection.LayoutChanged += QueueSelection;
        _selectionBorder.SizeChanged += (_, _) => QueueSelection();
        Content = _root;
        _host.ConfigureWindow(this);
        var frame = WindowFrame.Attach(this, _root).UseMica();
        frame.Header.IconSource = new SymbolIconSource { Symbol = Symbol.Page };
        frame.Header.MinHeight = frame.Header.Height = 40;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Standard;
        ConfigureDocumentStatus(frame);
        if (AppWindow.Presenter is OverlappedPresenter presenter) presenter.IsAlwaysOnTop = false;
        InkCanvas.Platform.Interop.NativeMethods.SetWindowPos(WinRT.Interop.WindowNative.GetWindowHandle(this),
            InkCanvas.Platform.Interop.NativeMethods.HWND_NOTOPMOST, 0, 0, 0, 0,
            InkCanvas.Platform.Interop.NativeMethods.SWP_NOMOVE | InkCanvas.Platform.Interop.NativeMethods.SWP_NOSIZE | InkCanvas.Platform.Interop.NativeMethods.SWP_NOACTIVATE);
        ConfigureAppearance();
        _detailTimer = DispatcherQueue.CreateTimer(); _detailTimer.Interval = TimeSpan.FromMilliseconds(170); _detailTimer.IsRepeating = false;
        _detailTimer.Tick += (_, _) => _ = RenderDetailAsync();
        _idleResources = new WindowIdleResources(this, ReleaseInactiveRendering, RestoreRendering);
        _paper.Draw += DrawPage;
        _ink.FrameDrawn += NavigationInkDrawn;
        _paper.CreateResources += (_, _) => { if (!_closed && _viewHasLayout) _ = RenderPageAsync(); };
        DeferredLayout.Observe(_viewport, () =>
        {
            if (_closed || _organizerOpen || _viewport.ActualWidth < 10 || _viewport.ActualHeight < 10) return;
            _viewHasLayout = true;
            StopScrollMotion();
            _navigationPictureDirty = true;
            if (_fitMode != FitMode.Free) Fit(_fitMode);
            else { ClampDocumentView(); Controller.NotifyElementsChanged(); }
            UpdateScrollbar();
            QueuePageRendering();
            UpdateSidebarLayout();
        });
        _ink.IsTabStop = true;
        _ink.InputStarting += NavigationInputStarting;
        ConfigureNavigation();
        _ink.InputBegan += () =>
        {
            _ink.Focus(FocusState.Pointer);
            InputBegan?.Invoke();
            if (Controller.AdvancedSelection is null) _selection.ClosePanels();
        };
        _ink.PointerWheelChanged += Wheel;
        _pageNumber.KeyDown += (_, args) =>
        {
            if (args.Key != Windows.System.VirtualKey.Enter) return;
            args.Handled = true;
            CommitPageNumber();
        };
        _pageNumber.LostFocus += (_, _) => CommitPageNumber();
        _pages.SelectionChanged += (_, _) =>
        {
            if (!_pageChanging && _pages.SelectedItem is ThumbnailItem item) OpenSourcePage(item.Index);
        };
        ConfigureSearchEvents();
        _searchResults.SelectionChanged += (_, _) => { if (_searchResults.SelectedItem is SearchItem item) ShowSearchHit(item.Hit); };
        _root.KeyDown += KeyDown;
        AppWindow.Closing += (sender, e) => { if (!_allowClose) { e.Cancel = true; _ = CloseAsync(); } };
        Closed += (_, _) =>
        {
            _closed = true; _lifetime.Cancel(); _renderCancellation?.Cancel(); _detailCancellation?.Cancel(); _searchCancellation?.Cancel(); _detailTimer.Stop();
            CancelSpacingDrag();
            _inkHistory.Changed -= QueueInkRouting;
            _inkHistory.Changed -= UpdateOrganizerCommands;
            StopScrollMotion(); ReleaseNavigationPicture(); ReleaseAppearance();
            _host.Detach(this);
            if (Controller is not null) Unbind(Controller);
            _ink.Dispose(); _detail?.Dispose(); _detail = null;
            ReleasePagePreviews();
            _thumbnailCancellation.Cancel();
            SuspendOrganizerThumbnails(clearCache: true);
            _paper.RemoveFromVisualTree();
            foreach (var controller in _controllers.Values) controller.Dispose();
            _controllers.Clear();
            _routedGeometry.Clear();
            _inkHistory.Clear();
            Cleanup = ReleaseAsync();
        };
        AutomationProperties.SetName(_pageNumber, "PDF 页码");
        AutomationProperties.SetName(_pages, "PDF 页面列表");
        AutomationProperties.SetName(_searchResults, "PDF 搜索结果");
        AutomationProperties.SetName(_scrollbar, "PDF 垂直滚动条");
        AutomationProperties.SetName(_horizontalScrollbar, "PDF 水平滚动条");
        _pageCount.Text = "/ " + file.Pages.Count;
        GoToPage(0);
        UpdateTitle();
        if (file.UnrecognizedInkPages > 0) SetStatus("部分旧墨迹数据无法编辑，原批注外观已保留");
    }

    private void Configure(InkController controller)
    {
        _host.ConfigureController(controller);
        controller.MultiTouchEnabled = Gestures.IsEnableMultiTouchMode;
        controller.PanZoomEnabled = true;
        controller.ConfigureGestures(Gestures.IsEnableTwoFingerZoom, Gestures.IsEnableTwoFingerTranslate, false);
        controller.ConfigureViewScaleLimits(.02f, 8);
        controller.CurrentAttributes.Color = InkColor.Red;
        if (_file.ReadOnly) controller.Tool = InkTool.Pan;
    }
    public void ApplySettings()
    {
        StopScrollMotion();
        _root.RequestedTheme = _host.Settings.Appearance.Theme switch { 1 => ElementTheme.Light, 2 => ElementTheme.Dark, _ => ElementTheme.Default };
        ApplyAppearance();
        foreach (var controller in _controllers.Values)
        {
            var color = controller.CurrentAttributes.Color;
            Configure(controller);
            controller.CurrentAttributes.Color = color;
        }
    }
    public void GoToPage(int index)
    {
        if ((uint)index < (uint)_layout.Count) OpenSourcePage(_layout.PageAt(index));
    }

    private void OpenSourcePage(int page)
    {
        if (_layout.PositionOf(page) < 0) return;
        if (_organizerOpen) SetOrganizerOpen(false);
        StopScrollMotion();
        ActivatePage(page, preserveView: false);
    }

    private void ActivatePage(int index, bool preserveView, Matrix3x2? viewOverride = null)
    {
        if (_closed || _layout.PositionOf(index) < 0) return;
        if (_spacingDrag is not null) return;
        if (Controller is not null && index == _pageIndex)
        {
            if (!preserveView && _viewHasLayout && !Controller.HasActiveInteraction) PositionPage(DocumentView);
            return;
        }
        _pageChanging = true;
        try
        {
            ClearSpacingSelection();
            var previous = Controller;
            var documentView = viewOverride ?? (previous is null ? Matrix3x2.Identity : _layout.DocumentView(_pageIndex, previous.ViewTransform));
            if (previous is not null)
            {
                if (previous.HasActiveInteraction) { _pageNumber.Text = (PageIndex + 1).ToString(); _pages.SelectedIndex = PageIndex; return; }
                previous.FinishDotAnimations(); previous.EndPlayback(); previous.ClearSelection(); Unbind(previous);
            }
            _pageIndex = index;
            if (!_controllers.TryGetValue(index, out var next))
            {
                next = PageController(index);
            }
            if (previous is not null)
            {
                previous.NotifyInputContextChanging();
                next.CurrentAttributes.Color = previous.CurrentAttributes.Color;
                next.CurrentAttributes.TaperKind = previous.CurrentAttributes.TaperKind;
                next.CurrentAttributes.Nib = previous.CurrentAttributes.Nib;
                next.CurrentAttributes.NibRotation = previous.CurrentAttributes.NibRotation;
                next.ConfigurePenWidths((float)_host.Settings.Canvas.InkWidth, (float)(_host.Settings.Canvas.HighlighterWidth ?? _host.Settings.Canvas.InkWidth));
                next.CurrentAttributes.HighlighterAlpha = previous.CurrentAttributes.HighlighterAlpha;
                next.EraserRadius = previous.EraserRadius; next.EraserMode = previous.EraserMode;
                next.ShapeKind = previous.ShapeKind; next.StickyShape = previous.StickyShape;
                next.PolygonVertexCount = previous.PolygonVertexCount;
                next.Tool = previous.Tool;
                next.CloneMode = previous.CloneMode;
            }
            Controller = next;
            Controller.Invalidated += OnInvalidated; Controller.SelectionChanged += QueueSelection;
            Controller.ToolChanged += OnToolChanged;
            _ink.Controller = next;
            _ink.ContentBounds = null;
            _pageNumber.Text = (PageIndex + 1).ToString();
            if (viewOverride is null) _pages.SelectedIndex = PageIndex;
            _generation++; _detailCancellation?.Cancel();
            _navigationPictureDirty = true;
            _detail?.Dispose(); _detail = null;
            if (preserveView) { next.ViewTransform = _layout.PageView(index, documentView); OnInvalidated(); }
            else if (_viewHasLayout) PositionPage(documentView);
            ControllerChanged?.Invoke(); QueueSelection(); _paper.Invalidate();
            UpdateDocumentStatus();
            UpdateScrollbar();
            QueuePageRendering();
            if (!_organizerOpen && _pages.SelectedItem is not null) _pages.ScrollIntoView(_pages.SelectedItem);
            ReadingProgressChanged?.Invoke();
        }
        finally { _pageChanging = false; }
    }
    private void Unbind(InkController controller)
    {
        controller.Invalidated -= OnInvalidated; controller.SelectionChanged -= QueueSelection;
        controller.ToolChanged -= OnToolChanged;
    }
    private void OnToolChanged()
    {
        StopScrollMotion();
        if (_spacingTool != SpacingTool.None) SetSpacingTool(SpacingTool.None);
        if (Controller.Tool != InkTool.Select) ClearSpacingSelection();
        if (!PageSpacing(_pageIndex).IsEmpty) InvalidateSpacing();
        if (_file.ReadOnly && Controller.Tool != InkTool.Pan) Controller.Tool = InkTool.Pan;
        UpdateDocumentStatus();
        UpdateReadingControls();
        QueueSelection();
    }
    private void OnInkChanged(int index)
    {
        if (!_routingInk) { _dirtyInkPages.Add(index); QueueInkRouting(); }
        _version++; _statusMessage = null; UpdateTitle();
        _navigationPictureDirty = true;
        _detailTimer.Stop(); _detailTimer.Start();
        UpdateInkBounds(index);
        _paper.Invalidate();
        InvalidateOrganizerThumbnail(index);
        if (_realizedThumbnails.TryGetValue(index, out var item)) _ = LoadThumbnailAsync(item);
    }
    private void UpdateTitle()
    {
        UpdateDocumentTitle();
        UpdateDocumentStatus();
    }

    private void OnInvalidated()
    {
        if (_closed || _organizerOpen || _layout.PositionOf(_pageIndex) < 0) return;
        if (_lastView != Controller.ViewTransform)
        {
            ClearSpacingSelection();
            float movement = Controller.ViewTransform.Translation.Y - _lastView.Translation.Y;
            if (Math.Abs(movement) > .1f) _previewDirection = movement < 0 ? 1 : -1;
            ClampDocumentView();
            ObserveNavigation();
            if (!_fittingView && !_pageChanging) _fitMode = FitMode.Free;
            _lastView = Controller.ViewTransform;
            QueueViewportUpdate();
        }
        if (!Controller.IsViewManipulating && !_scrollRendering) QueueSelection();
    }
    private void Fit(FitMode mode)
    {
        StopScrollMotion();
        _fitMode = mode;
        if (!_viewHasLayout || Controller is null) return;
        var page = _layout.SpreadBounds(_pageIndex);
        float availableWidth = (float)Math.Max(20, _viewport.ActualWidth - 32), availableHeight = (float)Math.Max(20, _viewport.ActualHeight - 32 - DocumentBottomPadding);
        float scale = Math.Clamp(mode == FitMode.Width ? availableWidth / page.Width : Math.Min(availableWidth / page.Width, availableHeight / page.Height), .02f, 8);
        _fittingView = true;
        try
        {
            var document = Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(
                ((float)_viewport.ActualWidth - page.Width * scale) / 2 - page.Left * scale,
                (mode == FitMode.Width ? 16 : ((float)_viewport.ActualHeight - DocumentBottomPadding - page.Height * scale) / 2) - page.Top * scale);
            Controller.ViewTransform = _layout.PageView(_pageIndex, document);
            Controller.NotifyElementsChanged();
        }
        finally { _fittingView = false; }
    }
    private void ZoomBy(float factor, Vector2? anchor = null)
    {
        if (Controller.HasActiveInteraction) return;
        StopScrollMotion();
        _fitMode = FitMode.Free;
        var pivot = anchor ?? new Vector2((float)_viewport.ActualWidth / 2, (float)_viewport.ActualHeight / 2);
        float current = MathF.Sqrt(MathF.Abs(Controller.ViewTransform.GetDeterminant()));
        factor = Math.Clamp(current * factor, .02f, 8) / current;
        Controller.ViewTransform *= Matrix3x2.CreateScale(factor, pivot);
        Controller.NotifyElementsChanged();
    }
    private void Wheel(object sender, PointerRoutedEventArgs e)
    {
        if (_spacingDrag is not null) { e.Handled = true; return; }
        if (Controller.HasActiveInteraction) { e.Handled = true; return; }
        var point = e.GetCurrentPoint(_ink);
        if ((e.KeyModifiers & Windows.System.VirtualKeyModifiers.Control) != 0)
            ZoomBy(MathF.Pow(1.15f, point.Properties.MouseWheelDelta / 120f), new((float)point.Position.X, (float)point.Position.Y));
        else
        {
            float delta = point.Properties.MouseWheelDelta / 120f * 70;
            var movement = point.Properties.IsHorizontalMouseWheel ? new Vector2(-delta, 0)
                : (e.KeyModifiers & Windows.System.VirtualKeyModifiers.Shift) != 0 ? new Vector2(delta, 0) : new Vector2(0, delta);
            SmoothScrollBy(movement);
        }
        e.Handled = true;
    }
    private async void KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_closed || e.Handled) return;
        if (InkCanvas.Platform.Interop.NativeMethods.GetKeyState(0x12) < 0 || InkCanvas.Platform.Interop.NativeMethods.GetKeyState(0x5B) < 0
            || InkCanvas.Platform.Interop.NativeMethods.GetKeyState(0x5C) < 0) return;
        bool control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        bool shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        if (_organizerOpen)
        {
            if (e.Key == Windows.System.VirtualKey.Escape) SetOrganizerOpen(false);
            else if (!control && e.Key == Windows.System.VirtualKey.Enter) OpenOrganizerSelection();
            else if (!control && e.Key == Windows.System.VirtualKey.Delete) DeleteOrganizerSelection();
            else if (control && e.Key == Windows.System.VirtualKey.Z && CanOrganizePages)
            { if (shift) Controller.Redo(); else Controller.Undo(); }
            else if (control && e.Key == Windows.System.VirtualKey.Y && CanOrganizePages) Controller.Redo();
            else if (control && e.Key == Windows.System.VirtualKey.S) { e.Handled = true; await SaveAsync(shift); }
            else if (control && e.Key == Windows.System.VirtualKey.O) { e.Handled = true; await _openDocument(); }
            else return;
            e.Handled = true;
            return;
        }
        if (e.Key == Windows.System.VirtualKey.Escape && _floatingSearch.Visibility == Visibility.Visible)
        { e.Handled = true; CloseSearch(); }
        else if (e.Key == Windows.System.VirtualKey.Escape && _spacingTool != SpacingTool.None)
        { e.Handled = true; SetSpacingTool(SpacingTool.None); }
        else if (control && e.Key == Windows.System.VirtualKey.O) { e.Handled = true; await _openDocument(); }
        else if (control && e.Key == Windows.System.VirtualKey.S) { e.Handled = true; await SaveAsync(shift); }
        else if (control && e.Key == Windows.System.VirtualKey.F) { e.Handled = true; OpenSearch(); }
        else if (control && e.Key is Windows.System.VirtualKey.Add or (Windows.System.VirtualKey)187)
        { e.Handled = true; ZoomBy(1.25f); }
        else if (control && e.Key is Windows.System.VirtualKey.Subtract or (Windows.System.VirtualKey)189)
        { e.Handled = true; ZoomBy(.8f); }
        else if (control && e.Key == Windows.System.VirtualKey.Number0) { e.Handled = true; Fit(FitMode.Page); }
        else if (e.Key == Windows.System.VirtualKey.F3) { e.Handled = true; NextSearchResult(shift ? -1 : 1); }
        else if (KeyboardShortcutScope.IsTextInput(e.OriginalSource as DependencyObject) || KeyboardShortcutScope.HasTextInputFocus(_root)) return;
        else if ((Controller.Tool != InkTool.Pan || _spacingTool != SpacingTool.None) && _spacingDrag is null && control && !shift && e.Key == Windows.System.VirtualKey.Z) { e.Handled = true; Controller.Undo(); }
        else if ((Controller.Tool != InkTool.Pan || _spacingTool != SpacingTool.None) && _spacingDrag is null && control && !shift && e.Key == Windows.System.VirtualKey.Y) { e.Handled = true; Controller.Redo(); }
        else if (Controller.Tool != InkTool.Pan && control && !shift && e.Key == Windows.System.VirtualKey.E)
        {
            e.Handled = true;
            ClearCanvas(inkOnly: false);
        }
        else if (!control && !shift)
        {
            if (e.Key == Windows.System.VirtualKey.PageDown) { e.Handled = true; NavigatePage(1); }
            else if (e.Key == Windows.System.VirtualKey.PageUp) { e.Handled = true; NavigatePage(-1); }
            else if (Controller.Tool != InkTool.Pan && e.Key == Windows.System.VirtualKey.Delete) { e.Handled = true; if (!TryDeleteLayoutSelection()) Controller.DeleteSelection(); }
            else if (Controller.Tool != InkTool.Pan && e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                if (!Controller.CancelAdvancedSelection()) { ClearSpacingSelection(); Controller.ClearSelection(); }
            }
        }
    }

    private async Task RenderDetailAsync()
    {
        if (_closed || _organizerOpen || !_idleResources.IsActive || !_viewHasLayout || !_pagePreviews.ContainsKey(_pageIndex)) return;
        var view = Controller.ViewTransform;
        if (Controller.IsViewManipulating || _scrollRendering) { _detailTimer.Start(); return; }
        if (!Matrix3x2.Invert(view, out var inverse)) return;
        int generation = _generation, index = _pageIndex;
        var page = _file.Pages[index];
        var corners = new[] { Vector2.Transform(new(-32, -32), inverse), Vector2.Transform(new((float)_viewport.ActualWidth + 32, -32), inverse),
            Vector2.Transform(new(-32, (float)_viewport.ActualHeight + 32), inverse),
            Vector2.Transform(new((float)_viewport.ActualWidth + 32, (float)_viewport.ActualHeight + 32), inverse) };
        var left = new Vector2(corners.Min(point => point.X), corners.Min(point => point.Y));
        var right = new Vector2(corners.Max(point => point.X), corners.Max(point => point.Y));
        var visible = DetailSourceBounds(index, new(left.X, left.Y, right.X, right.Y));
        float x = Math.Clamp(visible.Left, 0, page.DipWidth), y = Math.Clamp(visible.Top, 0, page.DipHeight);
        float width = Math.Clamp(visible.Right, 0, page.DipWidth) - x, height = Math.Clamp(visible.Bottom, 0, page.DipHeight) - y;
        if (width <= 0 || height <= 0) return;
        var source = new Rect(x, y, width, height);
        double scale = Math.Sqrt(Math.Abs(view.GetDeterminant())) * (_root.XamlRoot?.RasterizationScale ?? 1);
        scale = Math.Min(scale, Math.Min(Math.Sqrt(8_000_000d / (width * (double)height)), 8192d / Math.Max(width, height)));
        if (_pagePreviews.TryGetValue(index, out var preview))
        {
            var pixels = preview.Bitmap.SizeInPixels;
            double previewScale = Math.Min(pixels.Width / (double)page.DipWidth, pixels.Height / (double)page.DipHeight);
            if (previewScale >= scale * .99) return;
        }
        if (_detail is not null && _detailScale >= scale * .99 && _detailRect.Left <= source.Left && _detailRect.Right >= source.Right
            && _detailRect.Top <= source.Top && _detailRect.Bottom >= source.Bottom) return;
        _detailCancellation?.Cancel(); _detailCancellation?.Dispose();
        _detailCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var token = _detailCancellation.Token;
        try
        {
            var bitmap = await _rasterizer.RenderBitmapAsync(index, page, source, scale, token);
            if (_closed || token.IsCancellationRequested || generation != _generation || view != Controller.ViewTransform) { bitmap.Dispose(); return; }
            _detail?.Dispose(); _detail = bitmap; _detailRect = source; _detailScale = scale; _paper.Invalidate();
            _navigationPictureDirty = true;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && generation == _generation) SetStatus("高清页面渲染失败 · " + ex.Message); }
    }
    private async Task<CanvasBitmap> LoadBitmapAsync(byte[] bytes, CancellationToken token)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        { writer.WriteBytes(bytes); await writer.StoreAsync().AsTask(token); writer.DetachStream(); }
        stream.Seek(0);
        return await CanvasBitmap.LoadAsync(_paper, stream, 96).AsTask(token);
    }
    private void QueueSelection()
    {
        if (_closed || _selectionPending) return;
        _selectionPending = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () => { _selectionPending = false; if (!_closed) UpdateSelection(); });
    }
    private void UpdateSelection()
    {
        if (!Controller.HasSelection || Controller.Tool != InkTool.Select || Controller.Playback is not null || Controller.IsRegionSelecting || Controller.IsRotatingSelection)
        { _selectionBorder.Visibility = Visibility.Collapsed; return; }
        _selection.Refresh();
        _selectionBorder.Visibility = Visibility.Visible;
        _selectionBorder.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var size = _selectionBorder.DesiredSize;
        // Reuse the shared avoidance algorithm, with the main floating bar mapped into this viewport.
        double dpi = _root.XamlRoot?.RasterizationScale ?? 1;
        var origin = _viewport.TransformToVisual(_root).TransformPoint(new Windows.Foundation.Point());
        var client = AppWindow.ClientSize; var position = AppWindow.Position; var windowSize = AppWindow.Size;
        float screenX = position.X + (windowSize.Width - client.Width) / 2f + (float)(origin.X * dpi);
        float screenY = position.Y + windowSize.Height - client.Height - (windowSize.Width - client.Width) / 2f + (float)(origin.Y * dpi);
        var bar = _host.ToolbarScreenBounds;
        var obstacle = bar.IsEmpty ? Core.Ink.Rect.Empty : new Core.Ink.Rect((bar.Left - screenX) / (float)dpi, (bar.Top - screenY) / (float)dpi, (bar.Right - screenX) / (float)dpi, (bar.Bottom - screenY) / (float)dpi);
        float scale = (float)Math.Min(1, Math.Max(1, _viewport.ActualWidth - 16) / Math.Max(1, size.Width));
        _selectionBorder.RenderTransform = new ScaleTransform { ScaleX = scale, ScaleY = scale };
        var (first, second) = Controller.AdvancedReferencePositions();
        var place = FloatingToolbarLayout.Place(new Core.Ink.Rect(8, 8, Math.Max(9, (float)_viewport.ActualWidth - 8), Math.Max(9, (float)_viewport.ActualHeight - 8)),
            Controller.SelectionVisualBounds, obstacle, new((float)size.Width * scale, (float)size.Height * scale),
            firstReference: first, secondReference: second);
        Canvas.SetLeft(_selectionBorder, place.Left); Canvas.SetTop(_selectionBorder, place.Top);
    }
    private sealed record SearchItem(PdfSearchHit Hit, int Position)
    {
        public override string ToString() => "第 " + (Position + 1) + " 页\n" + Hit.Preview;
    }
    private sealed record BookmarkItem(PdfBookmark Bookmark)
    {
        public override string ToString() => new string(' ', Math.Min(6, Bookmark.Depth) * 2) + Bookmark.Title;
    }
    public async Task SearchAsync(string query)
    {
        _searchCancellation?.Cancel(); _searchCancellation?.Dispose();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); _searchCancellation = cancellation;
        try
        {
            query = query?.Trim();
            if (string.IsNullOrEmpty(query)) { _search.Text = ""; ClearSearch(); return; }
            _search.Text = query;
            _searching = true; UpdateSearchNavigation(); UpdateDocumentStatus();
            var sourceHits = await _file.SearchAsync(query, cancellation.Token, _pageOrder);
            if (_closed || cancellation.IsCancellationRequested) return;
            var hits = sourceHits.Where(hit => _layout.PositionOf(hit.PageIndex) >= 0)
                .OrderBy(hit => _layout.PositionOf(hit.PageIndex)).ToArray();
            _hits = hits;
            _navigationPictureDirty = true;
            _searchResults.ItemsSource = hits.Select(hit => new SearchItem(hit, _layout.PositionOf(hit.PageIndex))).ToArray();

            if (hits.Length > 0)
            {
                if (_searchResults.SelectedIndex == 0) ShowSearchHit(hits[0]);
                else _searchResults.SelectedIndex = 0;
            }
            _paper.Invalidate();
            UpdateSidebarView();
            if (hits.Length == 0) _searchCount.Text = "没有匹配结果";
            SetStatus("找到 " + hits.Length + " 个匹配结果" + (hits.Length == 0 ? " · 扫描件需要先进行文字识别" : hits.Length == 500 ? " · 最多显示 500 个结果" : ""));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && !cancellation.IsCancellationRequested) SetStatus("搜索失败 · " + ex.Message); }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
            { _searching = false; UpdateSearchNavigation(); UpdateDocumentStatus(); }
        }
    }

    public async Task<bool> SaveAsync(bool saveAs = false)
    {
        if (_closed || _saving || _file.ReadOnly) return false;
        if (_spacingDrag is not null || _controllers.Values.Any(controller => controller.HasActiveInteraction)) { SetStatus("请先完成当前笔画或拖动，再保存"); return false; }
        _saving = true;
        UpdateDocumentStatus();
        try
        {
            _searchCancellation?.Cancel();
            string path = _targetPath;
            if (saveAs || _file.HasSignatures && path.Equals(_file.SourcePath, StringComparison.OrdinalIgnoreCase))
            {
                var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(path) + " 批注" };
                picker.FileTypeChoices.Add("PDF 文档", new List<string> { ".pdf" });
                WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
                var result = await picker.PickSaveFileAsync(); if (result is null) return false;
                path = result.Path;
            }
            foreach (var controller in _controllers.Values) controller.FinishDotAnimations();
            var pages = new Dictionary<int, IReadOnlyList<InkStroke>>();
            NormalizeCrossPageInk();
            foreach (int page in _pageOrder)
                if (PageInk(page) is { Count: > 0 } ink) pages[page] = ink.Select(stroke => stroke.Clone()).ToArray();
            int version = _version;
            SetStatus("正在保存 PDF…");
            await _file.SaveAsync(path, pages,
                _pageRotations.Select((turn, index) => (turn, index)).ToDictionary(item => item.index, item => item.turn),
                new Dictionary<int, PdfPageSpacing>(_pageSpacing), _lifetime.Token, _pageOrder);
            _targetPath = path; _savedVersion = version; UpdateTitle();
            NotifyDocumentAi(path);
            SetStatus(IsDirty ? "已保存快照，新的批注尚未保存"
                : "已保存 · " + Path.GetFileName(path) + " · " + (new FileInfo(path).Length / 1024d).ToString("N0") + " KB");
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            if (!_closed) { SetStatus("保存失败 · " + ex.GetBaseException().Message); await MessageAsync("保存 PDF 失败", ex.GetBaseException().Message); }
            return false;
        }
        finally { _saving = false; UpdateDocumentStatus(); }
    }
    private Task<ContentDialogResult> MessageAsync(string title, string message)
    {
        var dialog = new ContentDialog { Title = title, Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, CloseButtonText = "关闭", XamlRoot = _root.XamlRoot };
        return dialog.ShowAsync().AsTask();
    }
    public async Task<bool> CloseAsync()
    {
        if (_closed) return true;
        if (_closing || _saving) return false;
        _closing = true;
        try
        {
            _searchCancellation?.Cancel();
            foreach (var controller in _controllers.Values) controller.FinishDotAnimations();
            if (IsDirty)
            {
                var dialog = new ContentDialog { Title = "保存 PDF 更改？", Content = Path.GetFileName(_targetPath) + " 有未保存的更改。",
                    PrimaryButtonText = "保存", SecondaryButtonText = "不保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary, XamlRoot = _root.XamlRoot };
                Activate();
                var answer = await dialog.ShowAsync();
                if (answer == ContentDialogResult.None) return false;
                if (answer == ContentDialogResult.Primary && !await SaveAsync()) return false;
            }
            if (ClosingForResume is not null) await ClosingForResume();
            _allowClose = true; Close(); await Cleanup; return true;
        }
        catch (Exception ex) { SetStatus("关闭失败 · " + ex.Message); return false; }
        finally { _closing = false; }
    }
    internal void ForceClose() { if (!_closed) { _allowClose = true; Close(); } }
    private async Task ReleaseAsync()
    {
        await _rasterizer.DisposeAsync();
        await Task.Run(_file.Dispose);
        _renderCancellation?.Dispose(); _detailCancellation?.Dispose(); _searchCancellation?.Dispose(); _thumbnailCancellation.Dispose(); _lifetime.Dispose();
    }
}
