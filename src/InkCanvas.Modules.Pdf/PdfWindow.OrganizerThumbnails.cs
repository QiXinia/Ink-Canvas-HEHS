using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    private const long OrganizerThumbnailBudget = 16 * 1024 * 1024;
    private const int OrganizerThumbnailLimit = 64;
    private readonly Dictionary<SelectorItem, ThumbnailItem> _organizerContainers = new();
    private readonly Dictionary<int, (WriteableBitmap Image, long Bytes, long Used)> _organizerCache = new();
    private readonly HashSet<int> _failedOrganizerThumbnails = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer _organizerTimer;
    private ScrollViewer _organizerScroll;
    private CancellationTokenSource _organizerRequest;
    private ThumbnailItem _renderingOrganizerItem;
    private bool _organizerWorkerRunning;
    private long _organizerClock, _organizerCacheBytes;
    internal int OrganizerRealizedCount => _organizerContainers.Count;
    internal int OrganizerReadyCount => _organizerContainers.Values.Count(item => item.Preview is not null);
    internal int OrganizerRenderCount { get; private set; }
    internal long OrganizerCacheBytes => _organizerCacheBytes;

    private void ConfigureOrganizerThumbnails()
    {
        _organizerTimer = DispatcherQueue.CreateTimer();
        _organizerTimer.Interval = TimeSpan.FromMilliseconds(55);
        _organizerTimer.IsRepeating = false;
        _organizerTimer.Tick += (_, _) => _ = RenderOrganizerThumbnailsAsync();
        _pageGrid.SizeChanged += (_, _) => QueueOrganizerThumbnails();
        _pageGrid.Loaded += (_, _) =>
        {
            if (_organizerScroll is null && FindScrollViewer(_pageGrid) is { } scroll)
            {
                _organizerScroll = scroll;
                scroll.ViewChanged += OrganizerViewChanged;
            }
            QueueOrganizerThumbnails();
        };
    }

    private static ScrollViewer FindScrollViewer(DependencyObject parent)
    {
        if (parent is ScrollViewer scroll) return scroll;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(parent, i)) is { } child) return child;
        return null;
    }

    private void OrganizerViewChanged(object sender, ScrollViewerViewChangedEventArgs args)
    {
        if (!OrganizerCanRender) return;
        if (_renderingOrganizerItem is { } item && !OrganizerItemIsVisible(item))
            _organizerRequest?.Cancel();
        QueueOrganizerThumbnails();
    }

    private bool OrganizerItemIsVisible(ThumbnailItem item)
    {
        if (_pageGrid.ItemsPanelRoot is not ItemsWrapGrid grid) return true;
        int position = _layout.PositionOf(item.Index);
        return position >= grid.FirstVisibleIndex && position <= grid.LastVisibleIndex;
    }

    private void OrganizerContainerChanged(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (_organizerContainers.Remove(args.ItemContainer, out var previous))
        {
            previous.Preview = null;
            if (ReferenceEquals(previous, _renderingOrganizerItem)) _organizerRequest?.Cancel();
        }
        if (args.InRecycleQueue || args.Item is not ThumbnailItem item) return;
        _organizerContainers[args.ItemContainer] = item;
        if (OrganizerCanRender && TryGetOrganizerThumbnail(item)) return;
        QueueOrganizerThumbnails();
    }

    private bool OrganizerCanRender => !_closed && _organizerOpen && _idleResources?.IsActive == true;

    private void QueueOrganizerThumbnails()
    {
        if (!OrganizerCanRender || _organizerWorkerRunning || _organizerTimer.IsRunning) return;
        _organizerTimer.Start();
    }

    private bool TryGetOrganizerThumbnail(ThumbnailItem item)
    {
        if (!_organizerCache.TryGetValue(item.Index, out var cached)) return false;
        _organizerCache[item.Index] = (cached.Image, cached.Bytes, ++_organizerClock);
        item.Preview = cached.Image;
        return true;
    }

    private async Task RenderOrganizerThumbnailsAsync()
    {
        if (_organizerWorkerRunning || !OrganizerCanRender) return;
        _organizerWorkerRunning = true;
        try
        {
            while (OrganizerCanRender)
            {
                ThumbnailItem item = null;
                foreach (var candidate in _organizerContainers.Values)
                {
                    if (candidate.Preview is not null || _failedOrganizerThumbnails.Contains(candidate.Index)) continue;
                    item ??= candidate;
                    if (!OrganizerItemIsVisible(candidate)) continue;
                    item = candidate;
                    break;
                }
                if (item is null) return;
                if (TryGetOrganizerThumbnail(item)) continue;
                _renderingOrganizerItem = item;
                using var request = CancellationTokenSource.CreateLinkedTokenSource(_thumbnailCancellation.Token);
                _organizerRequest = request;
                try
                {
                    // Fixed pixel tiers keep the cache independent of the window's column count.
                    double pixels = (_root.XamlRoot?.RasterizationScale ?? 1) > 1.25 ? 272 : 208;
                    var geometry = ThumbnailGeometry(item.Index);
                    var image = await RenderThumbnailAsync(item.Index, pixels / Math.Max(geometry.DipWidth, geometry.DipHeight), request.Token);
                    if (!OrganizerCanRender || request.IsCancellationRequested) continue;
                    long bytes = (long)image.PixelWidth * image.PixelHeight * 4;
                    OrganizerRenderCount++;
                    _organizerCache[item.Index] = (image, bytes, ++_organizerClock);
                    _organizerCacheBytes += bytes;
                    item.Preview = image;
                    while (_organizerCache.Count > OrganizerThumbnailLimit || _organizerCacheBytes > OrganizerThumbnailBudget)
                        RemoveOrganizerThumbnail(_organizerCache.MinBy(pair => pair.Value.Used).Key);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex)
                {
                    if (request.IsCancellationRequested || _closed) continue;
                    _failedOrganizerThumbnails.Add(item.Index);
                    Core.Diagnostics.FileLog.WriteLogToFile("PDF page organizer thumbnail: " + ex.Message);
                }
                finally { _organizerRequest = null; _renderingOrganizerItem = null; }
            }
        }
        finally { _organizerWorkerRunning = false; }
    }

    private void RemoveOrganizerThumbnail(int page)
    {
        if (_organizerCache.Remove(page, out var cached)) _organizerCacheBytes -= cached.Bytes;
    }

    private void InvalidateOrganizerThumbnail(int page)
    {
        RemoveOrganizerThumbnail(page);
        _failedOrganizerThumbnails.Remove(page);
        if (_organizerItems is null) return;
        var item = _organizerItems[page];
        item.Preview = null;
        if (ReferenceEquals(_renderingOrganizerItem, item)) _organizerRequest?.Cancel();
        QueueOrganizerThumbnails();
    }

    private void SuspendOrganizerThumbnails(bool clearCache)
    {
        _organizerTimer?.Stop();
        _organizerRequest?.Cancel();
        foreach (var item in _organizerContainers.Values) item.Preview = null;
        if (!clearCache) return;
        _organizerCache.Clear();
        _failedOrganizerThumbnails.Clear();
        _organizerCacheBytes = 0;
    }
}
