using InkCanvas.App.Views;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Platform.System;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Services;

public sealed partial class ShellController
{
    private readonly Dictionary<Guid, DesktopVideoWindow> _desktopVideos = new();
    private bool _videoPicking, _desktopMediaQueued;
    private CancellationTokenSource _videoImportCancellation;
    internal IReadOnlyCollection<DesktopVideoWindow> DesktopVideos => _desktopVideos.Values;

    public void InsertVideo() => _ = InsertVideoAsync();

    internal async Task InsertVideoAsync()
    {
        if (DocumentActive) { await NotifyAsync("插入视频", "请切换到桌面批注或画板后插入视频。"); return; }
        if (_videoPicking || _disposed) return;
        _videoPicking = true;
        using var cancellation = new CancellationTokenSource();
        _videoImportCancellation = cancellation;
        try
        {
            if (_board is not null) { await _board.InsertMediaAsync(); return; }
            var store = Services.ScreenImages;
            var page = store.Current;
            // Insert onto the toolbar's monitor, including when it is a secondary display.
            var monitor = ScreenService.FromWindow(WindowChrome.GetHwnd(_bar));
            float scale = Overlay.Window.DpiScale;
            var viewport = new System.Numerics.Vector2(monitor.Bounds.Width / scale, monitor.Bounds.Height / scale);
            var localView = page.ViewTransform * System.Numerics.Matrix3x2.CreateTranslation(
                (Overlay.Window.X - monitor.Bounds.X) / scale, (Overlay.Window.Y - monitor.Bounds.Y) / scale);
            var asset = await MediaImport.PickAsync(_bar, viewport, localView, cancellation.Token);
            if (asset is not null && IsCurrent() && await ImageImport.WaitForCanvasAsync(page, IsCurrent, cancellation.Token)) InsertDesktopMedia(asset);
            bool IsCurrent() => !_disposed && !_exiting && _board is null && !DocumentActive && ReferenceEquals(page, store.Current);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) await NotifyAsync("插入视频", "视频插入失败 · " + ex.Message); }
        finally { _videoPicking = false; if (ReferenceEquals(_videoImportCancellation, cancellation)) _videoImportCancellation = null; }
    }

    internal void InsertDesktopMedia(BoardMediaAsset asset)
    {
        if (!Services.ScreenImages.AddMedia(asset)) return;
        Overlay.SetInteractionMode(clickThrough: false, inkVisible: true);
        Services.OverlayInk.Tool = InkTool.Select;
        Services.OverlayInk.SelectElement(Services.ScreenImages.GetSelectionElements().Single(element => element.Id == asset.Id));
        RefreshDesktopMedia();
        if (_bar is not null) { _bar.Bar.ClickThroughActive = false; _bar.Bar.RefreshSelection(); }
    }

    private void QueueDesktopMedia()
    {
        if (_desktopMediaQueued || _disposed) return;
        if (_desktopVideos.Count == 0 && Services.ScreenImages.CurrentMedia.Count == 0) return;
        _desktopMediaQueued = true;
        _dispatcher.TryEnqueue(() => { _desktopMediaQueued = false; if (!_disposed) RefreshDesktopMedia(); });
    }

    private void OnDesktopMediaPlacement(BoardMediaAsset asset) => QueueDesktopMedia();

    private void RefreshDesktopMedia()
    {
        if (_disposed) return;
        var present = Services.ScreenImages.CurrentMedia;
        foreach (var id in _desktopVideos.Keys.Where(id => !present.Any(asset => asset.Id == id)).ToArray())
        { _desktopVideos[id].Close(); _desktopVideos.Remove(id); }
        bool visible = _board is null && !DocumentActive && Overlay.Window.IsVisible;
        if (!visible)
        {
            foreach (var window in _desktopVideos.Values) window.HideVideo();
            return;
        }
        nint previous = Overlay.Window.Handle;
        foreach (var asset in present.OrderByDescending(asset => asset.LayerOrder))
        {
            if (!_desktopVideos.TryGetValue(asset.Id, out var window))
                _desktopVideos.Add(asset.Id, window = new DesktopVideoWindow(Services.ScreenImages, asset));
            else if (!ReferenceEquals(window.Asset, asset))
            { window.Close(); _desktopVideos[asset.Id] = window = new DesktopVideoWindow(Services.ScreenImages, asset); }
            window.UpdatePlacement(Overlay);
            window.PlaceBelow(previous);
            previous = WindowChrome.GetHwnd(window);
        }
    }
}
