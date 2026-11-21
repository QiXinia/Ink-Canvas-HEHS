using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.Controls.Controls;

/// <summary>Standard left navigation and a scrollable page for application windows.</summary>
public sealed class WindowWorkspace : UserControl
{
    private readonly NavigationView _navigation = new()
    {
        PaneDisplayMode = NavigationViewPaneDisplayMode.Auto,
        ExpandedModeThresholdWidth = 850,
        CompactModeThresholdWidth = 560,
        OpenPaneLength = 220,
        CompactPaneLength = 48,
        IsSettingsVisible = false,
        IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
        IsPaneToggleButtonVisible = true,
        AlwaysShowHeader = true,
    };
    private readonly ScrollViewer _scroll = new()
    {
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        Padding = new Thickness(24, 8, 24, 24),
    };
    private readonly List<(string Name, Func<UIElement> Build)> _pages = new();
    private FrameworkElement _scrollTarget;
    public string SelectedPage { get; private set; }
    public AutoSuggestBox SearchBox
    {
        get => _navigation.AutoSuggestBox;
        set => _navigation.AutoSuggestBox = value;
    }

    public void FocusSearch()
    {
        _navigation.IsPaneOpen = true;
        DispatcherQueue.TryEnqueue(() => SearchBox?.Focus(FocusState.Programmatic));
    }

    public void RevealContent()
    {
        if (_navigation.DisplayMode != NavigationViewDisplayMode.Expanded) _navigation.IsPaneOpen = false;
    }

    public void ScrollToElement(FrameworkElement element)
    {
        _scrollTarget = element;
        _scroll.LayoutUpdated -= OnScrollLayoutUpdated;
        _scroll.LayoutUpdated += OnScrollLayoutUpdated;
        OnScrollLayoutUpdated(null, null);
    }

    private void OnScrollLayoutUpdated(object sender, object args)
    {
        if (_scrollTarget is null || _scroll.Content is not FrameworkElement page
            || _scroll.ViewportHeight <= 0 || _scroll.ExtentHeight <= 0) return;
        var origin = _scrollTarget.TransformToVisual(page).TransformPoint(default);
        // The first navigation request can arrive before the ScrollViewer computes its extent.
        if (origin.Y > _scroll.ViewportHeight && _scroll.ScrollableHeight <= 0) return;
        double offset = Math.Clamp(origin.Y - 12, 0, _scroll.ScrollableHeight);
        if (Math.Abs(_scroll.VerticalOffset - offset) > 1)
        {
            _scroll.ChangeView(null, offset, null, true);
            return;
        }
        _scroll.LayoutUpdated -= OnScrollLayoutUpdated;
        _scrollTarget = null;
    }
    public bool IsTitleBarAutoPaddingEnabled
    {
        get => _navigation.IsTitleBarAutoPaddingEnabled;
        set => _navigation.IsTitleBarAutoPaddingEnabled = value;
    }

    public WindowWorkspace()
    {
        _navigation.Content = _scroll;
        _navigation.SelectionChanged += (_, args) =>
        {
            if (args.SelectedItem is NavigationViewItem item && item.Tag is int index) ShowPage(index);
        };
        Content = _navigation;
    }

    public void AddPage(string name, string glyph, Func<UIElement> build)
    {
        int index = _pages.Count;
        _pages.Add((name, build));
        _navigation.MenuItems.Add(new NavigationViewItem { Content = name, Tag = index,
            Icon = new FontIcon { Glyph = glyph, IsTextScaleFactorEnabled = false } });
    }

    public void SelectPage(string name)
    {
        int index = _pages.FindIndex(page => page.Name == name);
        if (index < 0) return;
        if (ReferenceEquals(_navigation.SelectedItem, _navigation.MenuItems[index])) ShowPage(index);
        else _navigation.SelectedItem = _navigation.MenuItems[index];
    }

    private void ShowPage(int index)
    {
        _scroll.LayoutUpdated -= OnScrollLayoutUpdated;
        _scrollTarget = null;
        SelectedPage = _pages[index].Name;
        _navigation.Header = new TextBlock { Text = SelectedPage, FontSize = 28, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        _scroll.Content = _pages[index].Build();
        _scroll.ChangeView(null, 0, null, true);
    }
}
