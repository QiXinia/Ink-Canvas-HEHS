using System.ComponentModel;
using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Controls.Controls;
using InkCanvas.Ink.Rendering;
using InkCanvas.Pdf;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;

namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    private readonly CancellationTokenSource _thumbnailCancellation = new();
    private IReadOnlyList<PdfSearchHit> _hits = Array.Empty<PdfSearchHit>();
    private readonly TextBlock _searchCount = new() { Text = "输入文字后搜索", VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 2, TextWrapping = TextWrapping.Wrap };
    private readonly Dictionary<int, ThumbnailItem> _realizedThumbnails = new();
    private bool _sidebarUserSet;
    private TouchIconButton _sidebarToggle;
    private int _searchIndex = -1;
    public bool SidebarOpen => _split.IsPaneOpen;
    internal SplitViewDisplayMode SidebarMode => _split.DisplayMode;
    public int SearchResultCount => _hits.Count;
    public int RealizedThumbnailCount => _realizedThumbnails.Values.Count(item => item.Preview is not null);

    private sealed class ThumbnailItem(int index) : INotifyPropertyChanged
    {
        public int Index => index;
        public string Title => "第 " + (index + 1) + " 页";
        public CancellationTokenSource Cancellation;
        private ImageSource _preview;
        public ImageSource Preview
        {
            get => _preview;
            set { _preview = value; PropertyChanged?.Invoke(this, new(nameof(Preview))); }
        }
        public event PropertyChangedEventHandler PropertyChanged;
    }

    private StackPanel CreateSidebarHeader()
    {
        var header = new Grid { Margin = new Thickness(12, 4, 4, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "文档导航", FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        var collapse = Command("收起", "\uE76B", () => SetSidebarOpen(false));
        AutomationProperties.SetName(collapse, "收起 PDF 侧栏");
        Grid.SetColumn(collapse, 1); header.Children.Add(collapse);
        var panel = new StackPanel();
        panel.Children.Add(header); panel.Children.Add(_search);
        return panel;
    }

    private void ConfigureSidebar()
    {
        _pages.ItemsSource = Enumerable.Range(0, _file.Pages.Count).Select(index => new ThumbnailItem(index)).ToArray();
        _pages.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <StackPanel Width="164" Spacing="6" Margin="4,8">
                    <Border Height="148" Background="White" BorderBrush="#FFD0D0D0" BorderThickness="1">
                        <Image Source="{Binding Preview}" Stretch="Uniform" />
                    </Border>
                    <TextBlock Text="{Binding Title}" HorizontalAlignment="Center" />
                </StackPanel>
            </DataTemplate>
            """);
        _pages.ContainerContentChanging += (sender, args) =>
        {
            if (args.Item is not ThumbnailItem item) return;
            item.Cancellation?.Cancel();
            item.Preview = null;
            if (args.InRecycleQueue) _realizedThumbnails.Remove(item.Index);
            else
            {
                _realizedThumbnails[item.Index] = item;
                _ = LoadThumbnailAsync(item);
            }
        };
        _sidebar.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var searchRow = new Grid { Margin = new Thickness(8, 0, 8, 4) };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        searchRow.Children.Add(_searchCount);
        var previous = new Button { Content = "↑", MinWidth = 40, MinHeight = 44, Padding = new Thickness(4) };
        var next = new Button { Content = "↓", MinWidth = 40, MinHeight = 44, Padding = new Thickness(4) };
        AutomationProperties.SetName(previous, "上一个搜索结果"); AutomationProperties.SetName(next, "下一个搜索结果");
        ToolTipService.SetToolTip(previous, "上一个结果 (Shift+F3)"); ToolTipService.SetToolTip(next, "下一个结果 (F3)");
        previous.Click += (_, _) => NextSearchResult(-1); next.Click += (_, _) => NextSearchResult(1);
        Grid.SetColumn(previous, 1); Grid.SetColumn(next, 2); searchRow.Children.Add(previous); searchRow.Children.Add(next);
        Grid.SetRow(searchRow, 3); _sidebar.Children.Add(searchRow);
        _searchResults.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <TextBlock Text="{Binding}" TextWrapping="Wrap" MaxLines="5" Margin="4,8" />
            </DataTemplate>
            """);
        _root.SizeChanged += (_, _) => UpdateSidebarLayout();
        _split.PaneClosed += (_, _) => { _sidebarToggle.IsSelected = false; SuspendThumbnails(); };
        _split.PaneOpened += (_, _) => { _sidebarToggle.IsSelected = true; RefreshThumbnails(); };
    }

    public void SetSidebarOpen(bool open)
    {
        _sidebarUserSet = true;
        _split.IsPaneOpen = open;
        _sidebarToggle.IsSelected = open;
    }

    private void UpdateSidebarLayout()
    {
        // On narrow touch windows an overlay pane preserves a usable page viewport.
        var mode = _root.ActualWidth < 760 ? SplitViewDisplayMode.Overlay : SplitViewDisplayMode.Inline;
        if (_split.DisplayMode != mode)
        {
            _split.DisplayMode = mode;
            // Entering the narrow layout releases the page width. The page button can then
            // open the pane over the document without resizing or refitting the page.
            _split.IsPaneOpen = false;
        }
        if (!_sidebarUserSet && _root.ActualWidth > 0) _split.IsPaneOpen = _root.ActualWidth >= 760;
        _sidebarToggle.IsSelected = _split.IsPaneOpen;
    }

    private void SuspendThumbnails()
    {
        foreach (var item in _realizedThumbnails.Values) { item.Cancellation?.Cancel(); item.Preview = null; }
    }

    private void RefreshThumbnails()
    {
        if (!_split.IsPaneOpen || !_idleResources.IsActive) return;
        foreach (var item in _realizedThumbnails.Values) _ = LoadThumbnailAsync(item);
    }

    private async Task LoadThumbnailAsync(ThumbnailItem item)
    {
        if (_closed || !_split.IsPaneOpen || !_idleResources.IsActive) return;
        item.Cancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_thumbnailCancellation.Token);
        item.Cancellation = cancellation;
        try
        {
            await Task.Delay(80, cancellation.Token);
            var geometry = _file.Pages[item.Index];
            double scale = Math.Min(160 / geometry.DipWidth, 148 / geometry.DipHeight);
            var bytes = await _rasterizer.RenderAsync(item.Index, geometry, new(0, 0, geometry.DipWidth, geometry.DipHeight), scale, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            var strokes = _controllers.TryGetValue(item.Index, out var controller) ? controller.Document.Strokes : _file.GetInk(item.Index);
            using var stream = new InMemoryRandomAccessStream();
            if (strokes.Count > 0)
            {
                using var bitmap = await LoadBitmapAsync(bytes, cancellation.Token);
                using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), (float)bitmap.Size.Width, (float)bitmap.Size.Height, 96);
                using var scene = new InkSceneRenderer();
                using (var draw = target.CreateDrawingSession())
                {
                    draw.Clear(Microsoft.UI.Colors.White); draw.DrawImage(bitmap);
                    var transform = Matrix3x2.CreateScale((float)bitmap.Size.Width / geometry.DipWidth, (float)bitmap.Size.Height / geometry.DipHeight);
                    foreach (var stroke in strokes) scene.Strokes.Draw(draw, stroke, transform);
                }
                await target.SaveAsync(stream, CanvasBitmapFileFormat.Png).AsTask(cancellation.Token);
            }
            else
            {
                using var writer = new DataWriter(stream.GetOutputStreamAt(0));
                writer.WriteBytes(bytes); await writer.StoreAsync().AsTask(cancellation.Token); writer.DetachStream();
            }
            stream.Seek(0);
            var image = new BitmapImage { DecodePixelWidth = 160 };
            await image.SetSourceAsync(stream).AsTask(cancellation.Token);
            if (!_closed && !cancellation.IsCancellationRequested) item.Preview = image;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_closed && !cancellation.IsCancellationRequested) Core.Diagnostics.FileLog.WriteLogToFile("PDF thumbnail: " + ex.Message); }
        finally
        {
            if (ReferenceEquals(item.Cancellation, cancellation)) item.Cancellation = null;
            cancellation.Dispose();
        }
    }

    private void OpenSearch()
    {
        SetSidebarOpen(true);
        _search.Focus(FocusState.Programmatic);
        _searchResults.Visibility = _hits.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_hits.Count > 0) _pages.Visibility = _bookmarks.Visibility = Visibility.Collapsed;
    }

    private void ClearSearch()
    {
        _searchCancellation?.Cancel();
        _hits = Array.Empty<PdfSearchHit>(); _searchIndex = -1;
        _searchResults.ItemsSource = null;
        _searchCount.Text = "输入文字后搜索";
        _searchResults.Visibility = _bookmarks.Visibility = Visibility.Collapsed; _pages.Visibility = Visibility.Visible;
        _paper.Invalidate();
    }

    public void NextSearchResult(int direction)
    {
        if (_hits.Count == 0) return;
        _searchIndex = (_searchIndex + direction + _hits.Count) % _hits.Count;
        _searchResults.SelectedIndex = _searchIndex;
        _searchResults.ScrollIntoView(_searchResults.SelectedItem);
        ShowSearchHit(_hits[_searchIndex]);
    }

    private void ShowSearchHit(PdfSearchHit hit)
    {
        if (Controller.HasActiveInteraction) return;
        _searchIndex = _hits.ToList().IndexOf(hit);
        GoToPage(hit.PageIndex);
        if (hit.Bounds is { Count: > 0 })
        {
            var center = hit.Bounds[0].Center;
            var point = Vector2.Transform(center, Controller.ViewTransform);
            Controller.ViewTransform *= Matrix3x2.CreateTranslation((float)_viewport.ActualWidth / 2 - point.X, (float)_viewport.ActualHeight / 2 - point.Y);
            _fitMode = FitMode.Free;
            Controller.NotifyElementsChanged();
        }
        _searchCount.Text = (_searchIndex + 1) + " / " + _hits.Count;
        _paper.Invalidate();
    }

    private void DrawSearchHighlights(CanvasDrawingSession draw, int page)
    {
        foreach (var hit in _hits.Where(hit => hit.PageIndex == page))
        foreach (var bounds in hit.Bounds ?? Array.Empty<Core.Ink.Rect>())
        {
            bool selected = _searchIndex >= 0 && ReferenceEquals(_hits[_searchIndex], hit);
            draw.FillRectangle(bounds.Left, bounds.Top, bounds.Width, bounds.Height,
                selected ? Windows.UI.Color.FromArgb(140, 255, 150, 0) : Windows.UI.Color.FromArgb(100, 255, 225, 0));
        }
    }
}
