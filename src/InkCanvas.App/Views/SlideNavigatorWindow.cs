using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Features.Ppt;
using InkCanvas.Platform.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics;
using Windows.Storage.Streams;

namespace InkCanvas.App.Views;

/// <summary>
/// 课件页导航面板：按「全部 / 仅批注 / 收藏」筛选课件页，缩略图 + 页名 + 批注数，
/// 点击跳转，星标收藏。面板是独立浮窗，覆盖在放映窗口之上但不抢焦点。
/// </summary>
public sealed class SlideNavigatorWindow : Window
{
    private const int WidthDip = 348;
    private const double HeightRatio = 0.72;

    private readonly StackPanel _list = new() { Spacing = 6 };
    private readonly TextBlock _emptyText;
    private readonly Button _allFilter;
    private readonly Button _annotatedFilter;
    private readonly Button _favoriteFilter;
    private readonly Border _root;
    private readonly Dictionary<string, BitmapImage> _thumbCache = new();
    private readonly List<InMemoryRandomAccessStream> _streams = new();

    private SlideContentIndex _index;
    private SlideFilter _filter = SlideFilter.All;
    private int _currentSlideId;

    public event Action<int> SlideActivated;

    public event Action<int> FavoriteToggled;

    /// <summary>点击带 AI 讲解的页时请求重新调出该页讲解。</summary>
    public event Action<int> AiReviewRequested;

    private readonly TextBlock _titleText = new()
    {
        Text = "课件页导航",
        FontSize = 14,
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public SlideNavigatorWindow(int theme)
    {
        Title = "课件页";

        _allFilter = FilterButton("全部", SlideFilter.All);
        _annotatedFilter = FilterButton("仅批注", SlideFilter.Annotated);
        _favoriteFilter = FilterButton("收藏", SlideFilter.Favorite);
        var filters = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 8),
        };
        filters.Children.Add(_allFilter);
        filters.Children.Add(_annotatedFilter);
        filters.Children.Add(_favoriteFilter);

        _emptyText = new TextBlock
        {
            Text = "当前筛选下没有课件页",
            FontSize = 12,
            Foreground = Resolve("IcTextSecondaryBrush"),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 0),
        };

        var body = new StackPanel { Spacing = 0 };
        body.Children.Add(filters);
        body.Children.Add(_list);
        body.Children.Add(_emptyText);

        var scroll = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Padding = new Thickness(12, 6, 12, 12),
        };

        var close = new Button
        {
            Content = new TextBlock { Text = "✕", FontSize = 12 },
            Width = 30,
            Height = 28,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        close.Click += (_, _) => HidePanel();

        var title = _titleText;
        ToolTipService.SetToolTip(close, "关闭（Esc）");

        var header = new Grid { Height = 40, Margin = new Thickness(12, 0, 8, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0);
        Grid.SetColumn(close, 1);
        header.Children.Add(title);
        header.Children.Add(close);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(header, 0);
        Grid.SetRow(scroll, 1);
        layout.Children.Add(header);
        layout.Children.Add(scroll);

        _root = new Border
        {
            Background = Resolve("IcSurfaceAltBrush"),
            BorderBrush = Resolve("IcStrokeBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Child = layout,
        };
        Content = _root;

        WindowChrome.MakeBorderless(this, topmost: true, resizable: false);
        WindowChrome.ApplyTheme(this, theme);

        var escape = new KeyboardAccelerator { Key = Windows.System.VirtualKey.Escape };
        escape.Invoked += (_, e) => { HidePanel(); e.Handled = true; };
        _root.KeyboardAccelerators.Add(escape);

        base.Closed += (_, _) => DisposeThumbs();
        UpdateFilterVisual();
        SetVisible(false);
    }

    /// <summary>刷新索引与当前页；面板可见时才重建列表，避免隐藏时空转。</summary>
    public void Update(SlideContentIndex index, int currentSlideId)
    {
        _index = index?.Clone();
        _currentSlideId = currentSlideId;
        if (_index is not null)
        {
            int currentIndex = 0;
            foreach (SlideContentEntry entry in _index.Slides)
            {
                if (entry.SlideId == currentSlideId)
                {
                    currentIndex = entry.Index;
                    break;
                }
            }

            _titleText.Text = currentIndex > 0
                ? "课件页导航 · 第 " + currentIndex + " / " + _index.Count + " 页 · 放映中"
                : "课件页导航 · 共 " + _index.Count + " 页 · 放映中";
        }

        if (IsVisible) Rebuild();
    }

    public bool IsVisible { get; private set; }

    public void Toggle()
    {
        if (IsVisible) HidePanel();
        else ShowPanel();
    }

    public void ShowPanel()
    {
        Rebuild();
        SetVisible(true);
        Place();
    }

    public void HidePanel() => SetVisible(false);

    private void SetFilter(SlideFilter filter)
    {
        _filter = filter;
        UpdateFilterVisual();
        Rebuild();
    }

    private void UpdateFilterVisual()
    {
        ApplyFilterStyle(_allFilter, _filter == SlideFilter.All);
        ApplyFilterStyle(_annotatedFilter, _filter == SlideFilter.Annotated);
        ApplyFilterStyle(_favoriteFilter, _filter == SlideFilter.Favorite);
    }

    private void ApplyFilterStyle(Button button, bool active)
    {
        button.Background = active ? Resolve("IcAccentSoftBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        button.Foreground = active ? Resolve("IcAccentBrush") : Resolve("IcTextSecondaryBrush");
        button.BorderBrush = active ? Resolve("IcAccentBrush") : Resolve("IcStrokeBrush");
    }

    private Button FilterButton(string text, SlideFilter filter)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 32,
            MinWidth = 64,
            Padding = new Thickness(10, 0, 10, 0),
            FontSize = 12,
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
        };
        button.Click += (_, _) => SetFilter(filter);
        return button;
    }

    private void Rebuild()
    {
        _list.Children.Clear();

        IReadOnlyList<SlideContentEntry> items = _index?.Filter(_filter) ?? Array.Empty<SlideContentEntry>();
        _emptyText.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (SlideContentEntry entry in items)
            _list.Children.Add(BuildRow(entry));
    }

    private FrameworkElement BuildRow(SlideContentEntry entry)
    {
        bool current = entry.SlideId > 0 && entry.SlideId == _currentSlideId;

        var image = new Image
        {
            Width = 112,
            Height = 63,
            Stretch = Stretch.UniformToFill,
        };
        if (!string.IsNullOrEmpty(entry.ThumbnailPath))
        {
            if (_thumbCache.TryGetValue(entry.ThumbnailPath, out BitmapImage cached)) image.Source = cached;
            else _ = LoadThumbAsync(entry.ThumbnailPath, image);
        }

        var imageHost = new Border
        {
            Width = 112,
            Height = 63,
            Background = Resolve("IcSurfaceBrush"),
            CornerRadius = new CornerRadius(6),
            Child = image,
        };

        var info = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        info.Children.Add(new TextBlock
        {
            Text = "第 " + entry.Index + " 页",
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,

        });
        if (!string.IsNullOrEmpty(entry.Name))
        {
            info.Children.Add(new TextBlock
            {
                Text = entry.Name,
                FontSize = 11,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = Resolve("IcTextSecondaryBrush"),
            });
        }

        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        if (entry.HasInk)
        {
            badges.Children.Add(new TextBlock
            {
                Text = "✎ " + entry.InkStrokeCount,
                FontSize = 10.5,
                Foreground = Resolve("IcAccentBrush"),
            });
        }

        if (entry.HasAi)
        {
            badges.Children.Add(AiGlyph.CreateLabel(AiGlyph.BadgeLabelSize, Resolve("IcAccentBrush")));
        }

        if (badges.Children.Count > 0) info.Children.Add(badges);

        var star = new Button
        {
            Content = new TextBlock
            {
                Text = entry.Favorite ? "★" : "☆",
                FontSize = 15,
                Foreground = entry.Favorite ? Resolve("IcAccentBrush") : Resolve("IcTextSecondaryBrush"),
            },
            Width = 34,
            Height = 34,
            Padding = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(star, entry.Favorite ? "取消收藏" : "收藏本页");
        star.Click += (_, _) => FavoriteToggled?.Invoke(entry.SlideId);

        var grid = new Grid { ColumnSpacing = 8 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(imageHost, 0);
        Grid.SetColumn(info, 1);
        Grid.SetColumn(star, 2);
        grid.Children.Add(imageHost);
        grid.Children.Add(info);
        grid.Children.Add(star);

        var row = new Button
        {
            Content = grid,
            Padding = new Thickness(6),
            MinHeight = 75,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Background = current ? Resolve("IcAccentSoftBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = current ? Resolve("IcAccentBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
        };
        row.Click += (_, _) =>
        {
            SlideActivated?.Invoke(entry.Index);
            if (entry.HasAi) AiReviewRequested?.Invoke(entry.SlideId);
        };
        return row;
    }

    private async Task LoadThumbAsync(string path, Image target)
    {
        try
        {
            if (!File.Exists(path)) return;

            byte[] bytes = await File.ReadAllBytesAsync(path);
            var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(bytes);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }

            stream.Seek(0);
            var bitmap = new BitmapImage();
            await bitmap.SetSourceAsync(stream);

            _thumbCache[path] = bitmap;
            _streams.Add(stream);
            target.Source = bitmap;
        }
        catch (Exception)
        {
            // 缩略图损坏时保留占位，不影响跳转。
        }
    }

    private void Place()
    {
        var work = ScreenService.WorkArea;
        double dpi = WindowChrome.GetDpiScale(this);
        int width = (int)Math.Round(WidthDip * dpi);
        int height = (int)Math.Round(work.Height * HeightRatio);
        int x = work.X + work.Width - width - (int)Math.Round(12 * dpi);
        int y = work.Y + (work.Height - height) / 2;
        if (AppWindow.Size.Width != width || AppWindow.Size.Height != height)
            WindowChrome.Place(this, x, y, width, height);
        else
            AppWindow.Move(new PointInt32(x, y));
    }

    private void SetVisible(bool visible)
    {
        IsVisible = visible;
        if (visible)
        {
            Platform.Interop.NativeMethods.ShowWindow(
                WindowChrome.GetHwnd(this), Platform.Interop.NativeMethods.SW_SHOWNOACTIVATE);
        }
        else
        {
            Platform.Interop.NativeMethods.ShowWindow(
                WindowChrome.GetHwnd(this), Platform.Interop.NativeMethods.SW_HIDE);
        }
    }

    private void DisposeThumbs()
    {
        foreach (InMemoryRandomAccessStream stream in _streams)
        {
            try { stream.Dispose(); }
            catch (Exception) { }
        }

        _streams.Clear();
        _thumbCache.Clear();
    }

    private static Brush Resolve(string key)
        => Application.Current.Resources[key] as Brush ?? new SolidColorBrush(Microsoft.UI.Colors.Transparent);
}