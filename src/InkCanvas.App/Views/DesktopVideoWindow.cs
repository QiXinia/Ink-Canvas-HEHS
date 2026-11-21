using System.Numerics;
using System.Runtime.InteropServices;
using InkCanvas.App.Services;
using InkCanvas.Features.Board;
using InkCanvas.Platform.Interop;
using InkCanvas.Platform.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Views;

/// <summary>Hardware-decoded desktop video below the ink overlay, with the normal board media controls.</summary>
internal sealed class DesktopVideoWindow : Window
{
    private readonly BoardMediaLayer _media = new();
    private readonly BoardMediaAsset _asset;
    private ScreenRect _bounds;
    private Matrix3x2 _transform;
    private float _width, _height, _left, _top;
    private float[] _assetTransform;
    private double _dpi;
    private bool _shown, _closed;
    private bool _resumePlayback;
    private byte[] _data;
    public BoardMediaAsset Asset => _asset;
    internal BoardMediaLayer MediaLayer => _media;

    public DesktopVideoWindow(BoardService board, BoardMediaAsset asset)
    {
        _asset = asset;
        Title = asset.Name + " — Ink Canvas";
        Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Children = { _media } };
        WindowChrome.MakeBorderless(this, noActivate: true);
        _media.SetPage(board, asset.Id);
        _data = asset.Data;
        Closed += (_, _) => { _closed = true; _media.Dispose(); };
    }

    internal void UpdatePlacement(OverlayHost overlay)
    {
        if (_closed) return;
        var asset = _asset;
        var physical = BoardElementMath.Read(asset.Transform) * overlay.Controller.ViewTransform
            * Matrix3x2.CreateScale(overlay.Window.DpiScale) * Matrix3x2.CreateTranslation(overlay.Window.X, overlay.Window.Y);
        var corners = new[] { new Vector2(asset.Left, asset.Top), new Vector2(asset.Left + asset.Width, asset.Top),
            new Vector2(asset.Left + asset.Width, asset.Top + asset.Height), new Vector2(asset.Left, asset.Top + asset.Height) };
        for (int index = 0; index < corners.Length; index++) corners[index] = Vector2.Transform(corners[index], physical);
        var desktop = ScreenService.Virtual;
        int left = (int)MathF.Max(desktop.X, MathF.Floor(corners.Min(point => point.X)));
        int top = (int)MathF.Max(desktop.Y, MathF.Floor(corners.Min(point => point.Y)));
        int right = (int)MathF.Min(desktop.X + desktop.Width, MathF.Ceiling(corners.Max(point => point.X)));
        int bottom = (int)MathF.Min(desktop.Y + desktop.Height, MathF.Ceiling(corners.Max(point => point.Y)));
        if (right <= left || bottom <= top) { HideVideo(); return; }
        var bounds = new ScreenRect(left, top, right - left, bottom - top);
        if (bounds != _bounds) WindowChrome.PlaceClient(this, left, top, bounds.Width, bounds.Height);
        double dpi = WindowChrome.GetDpiScale(this);
        var view = overlay.Controller.ViewTransform * Matrix3x2.CreateScale(overlay.Window.DpiScale / (float)dpi)
            * Matrix3x2.CreateTranslation((overlay.Window.X - left) / (float)dpi, (overlay.Window.Y - top) / (float)dpi);
        bool changed = bounds != _bounds || view != _transform || _dpi != dpi || _width != asset.Width || _height != asset.Height
            || _left != asset.Left || _top != asset.Top || !BoardElementMath.Same(_assetTransform, asset.Transform);
        if (changed)
        {
            _bounds = bounds; _transform = view; _dpi = dpi;
            _width = asset.Width; _height = asset.Height; _left = asset.Left; _top = asset.Top; _assetTransform = asset.Transform?.ToArray();
            _media.UpdateViewTransform(view);
            _media.UpdateElement(asset);
            var origin = WindowChrome.GetClientPosition(this);
            var frame = AppWindow.Position;
            var polygon = corners.Select(point => new NativeMethods.POINT
                { X = (int)MathF.Round(point.X - left) + origin.X - frame.X, Y = (int)MathF.Round(point.Y - top) + origin.Y - frame.Y }).ToArray();
            nint region = CreatePolygonRgn(polygon, polygon.Length, 1);
            if (region != 0 && SetWindowRgn(WindowChrome.GetHwnd(this), region, true) == 0) NativeMethods.DeleteObject(region);
        }
        if (!ReferenceEquals(_data, asset.Data)) { _data = asset.Data; _media.Refresh(); }
        if (!_shown)
        {
            AppWindow.Show(activateWindow: false); _shown = true;
            if (_resumePlayback) _media.GetPlayer(_asset)?.Play();
            _resumePlayback = false;
        }
    }

    internal void PlaceBelow(nint previous)
    {
        if (_closed || !_shown) return;
        var hwnd = WindowChrome.GetHwnd(this);
        if (NativeMethods.GetWindow(hwnd, 3 /* GW_HWNDPREV */) != previous)
            NativeMethods.SetWindowPos(hwnd, previous, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    internal void HideVideo()
    {
        if (_closed || !_shown) return;
        var player = _media.GetPlayer(_asset);
        _resumePlayback = player?.PlaybackSession.PlaybackState == Windows.Media.Playback.MediaPlaybackState.Playing;
        player?.Pause();
        AppWindow.Hide(); _shown = false;
    }

    [DllImport("gdi32.dll")] private static extern nint CreatePolygonRgn(NativeMethods.POINT[] points, int count, int mode);
    [DllImport("user32.dll")] private static extern int SetWindowRgn(nint hwnd, nint region, bool redraw);
}
