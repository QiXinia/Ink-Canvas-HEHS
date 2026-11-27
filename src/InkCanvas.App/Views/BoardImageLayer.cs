using InkCanvas.Controls.Theming;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using Windows.Storage.Streams;

namespace InkCanvas.App.Views;

/// <summary>
/// Per-page raster layer. Images remain in the same DIP coordinate space as board ink.
/// Rotate/flip/gesture deltas accumulate in <see cref="BoardImageAsset.Transform"/>, the port of
/// the WPF element RenderTransform TransformGroup (MW_SelectionGestures.cs ApplyElementMatrixTransform).
/// </summary>
internal sealed class BoardImageLayer : Canvas
{
    private readonly Dictionary<Guid, Grid> _views = new();
    private readonly HashSet<(Guid Id, int Version)> _loading = new();
    private readonly Dictionary<byte[], Task<Microsoft.UI.Xaml.Media.Imaging.BitmapImage>> _bitmapSources = new();
    private BoardService _board;
    private BoardImageAsset _selected;
    private int _refreshVersion;
    public bool RenderInScene { get; set; }

    public BoardImageLayer() => Background = null;

    public BoardImageAsset SelectedImage => _selected;
    public void SetSelection(IEnumerable<BoardImageAsset> assets)
    {
        _selected = assets.FirstOrDefault();
        foreach (var view in _views.Values) SetSelectedVisual(view, false);
    }

    public event Action SelectionChanged;

    public void SetPage(BoardService board)
    {
        _board = board;
        _selected = null;
        Children.Clear();
        _views.Clear();
        _bitmapSources.Clear();
        _refreshVersion++;
        Refresh();
    }

    public void UpdateViewTransform(Matrix3x2 transform)
    {
        if (RenderTransform is not MatrixTransform matrix) RenderTransform = matrix = new MatrixTransform();
        matrix.Matrix = new Matrix(transform.M11, transform.M12, transform.M21, transform.M22, transform.M31, transform.M32);
    }

    /// <summary>
    /// Reconcile views with the page's assets instead of rebuilding: survives undo/transform
    /// without re-decoding bitmaps, and keeps the current selection (and its panel) alive.
    /// </summary>
    public void Refresh()
    {
        if (RenderInScene) return;
        if (_board is null)
        {
            Children.Clear();
            _views.Clear();
            return;
        }
        var present = new HashSet<Guid>();
        foreach (var asset in _board.CurrentImages) present.Add(asset.Id);
        var sources = _board.CurrentImages.Select(asset => asset.Data).ToHashSet();
        foreach (var data in _bitmapSources.Keys.Where(data => !sources.Contains(data)).ToArray()) _bitmapSources.Remove(data);
        foreach (var id in _views.Keys.ToList())
        {
            if (present.Contains(id)) continue;
            Children.Remove(_views[id]);
            _views.Remove(id);
        }
        int version = _refreshVersion;
        int stackIndex = 0;
        foreach (var asset in _board.CurrentImages)
        {
            if (_views.TryGetValue(asset.Id, out var view)) { ApplyGeometry(view, asset); Canvas.SetZIndex(view, stackIndex); }
            else if (_loading.Add((asset.Id, version))) _ = AddViewAsync(asset, version);
            stackIndex++;
        }
        if (_selected is not null && !present.Contains(_selected.Id))
        {
            _selected = null;
            SelectionChanged?.Invoke();
        }
    }

    /// <summary>Re-apply one asset's placement/transform to its live view (gesture/undo feedback).</summary>
    public void UpdateElement(BoardImageAsset asset)
    {
        if (asset is not null && _views.TryGetValue(asset.Id, out var view))
            ApplyGeometry(view, asset);
    }

    private static void ApplyGeometry(Grid view, BoardImageAsset asset)
    {
        view.Width = asset.Width;
        view.Height = asset.Height;
        view.Opacity = Math.Clamp(asset.Opacity, 0, 1);
        Canvas.SetLeft(view, asset.Left);
        Canvas.SetTop(view, asset.Top);
        // Canvas-space element matrix expressed as a local RenderTransform (origin top-left).
        var m = BoardElementMath.RenderMatrix(asset.Left, asset.Top, asset.Transform);
        view.RenderTransformOrigin = new Windows.Foundation.Point(0, 0);
        if (view.RenderTransform is not MatrixTransform matrix) view.RenderTransform = matrix = new MatrixTransform();
        matrix.Matrix = new Matrix(m.M11, m.M12, m.M21, m.M22, m.M31, m.M32);
    }

    private async Task AddViewAsync(BoardImageAsset asset, int version)
    {
        try
        {
            if (!_bitmapSources.TryGetValue(asset.Data, out var source))
                _bitmapSources[asset.Data] = source = LoadBitmapAsync(asset.Data);
            var bitmap = await source;
            if (version != _refreshVersion || !_board.CurrentImages.Contains(asset)) return;
            var image = new Image { Source = bitmap, Stretch = Stretch.Uniform };
            var border = new Border
            {
                BorderThickness = new Thickness(2),
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            };
            var handle = new Border
            {
                Width = 12,
                Height = 12,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Background = PopupBrushes.Accent(),
                Visibility = Visibility.Collapsed,
            };
            var view = new Grid
            {
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Tag = asset,
            };
            view.Children.Add(image);
            view.Children.Add(border);
            view.Children.Add(handle);
            // Selection input is routed by the common board selection host.
            ApplyGeometry(view, asset);
            _views[asset.Id] = view;
            Canvas.SetZIndex(view, _board.CurrentImages.ToList().IndexOf(asset));
            Children.Add(view);
            SetSelectedVisual(view, false);
        }
        catch
        {
            // Keep the rest of the board usable if an embedded image is corrupt or unsupported.
        }
        finally { _loading.Remove((asset.Id, version)); }
    }

    private static async Task<Microsoft.UI.Xaml.Media.Imaging.BitmapImage> LoadBitmapAsync(byte[] bytes)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    /// <summary>Drop the selection without deleting (mode switch or outside click).</summary>
    public void ClearSelection()
    {
        if (_selected is null) return;
        _selected = null;
        foreach (var view in _views.Values) SetSelectedVisual(view, false);
        SelectionChanged?.Invoke();
    }

    private static void SetSelectedVisual(Grid view, bool selected)
    {
        if (view.Children.Count < 3) return;
        if (view.Children[1] is Border border)
        {
            border.BorderBrush = selected
                ? PopupBrushes.Accent()
                : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        }
        if (view.Children[2] is Border handle) handle.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
    }
}
