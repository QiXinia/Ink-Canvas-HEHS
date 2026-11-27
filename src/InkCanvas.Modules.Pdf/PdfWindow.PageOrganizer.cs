using System.Collections.ObjectModel;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.History;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;

namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    private readonly Grid _organizer = new() { Visibility = Visibility.Collapsed };
    private readonly GridView _pageGrid = new() { SelectionMode = ListViewSelectionMode.Multiple, IsSwipeEnabled = true };
    private ObservableCollection<ThumbnailItem> _gridItems = new();
    private readonly TextBlock _organizerCount = new() { VerticalAlignment = VerticalAlignment.Center, FontSize = 13,
        TextTrimming = TextTrimming.CharacterEllipsis };
    private ThumbnailItem[] _organizerItems;
    private int[] _pageOrder;
    private bool _organizerOpen, _syncingPageGrid, _organizerNeedsPosition;
    private TouchIconButton _organizerToggle, _organizerOpenPage, _organizerPrevious, _organizerNext,
        _organizerRotateLeft, _organizerRotateRight, _organizerDelete, _organizerUndo, _organizerRedo;
    internal bool OrganizerOpen => _organizerOpen;
    internal IReadOnlyList<int> PageOrder => _pageOrder;

    private void ConfigurePageOrganizer()
    {
        _organizer.RowDefinitions.Add(new() { Height = GridLength.Auto });
        _organizer.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var controls = new Grid { Margin = new Thickness(16, 8, 16, 4), ColumnSpacing = 8 };
        controls.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var title = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new TextBlock { Text = "页面管理", FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        title.Children.Add(_organizerCount);
        controls.Children.Add(title);
        var actions = CommandGroup();
        _organizerOpenPage = OrganizerCommand("打开选中页", "打开", Symbol.OpenFile, OpenOrganizerSelection);
        _organizerPrevious = OrganizerCommand("前移选中页", "前移", Symbol.Back, () => MoveOrganizerSelection(-1));
        _organizerNext = OrganizerCommand("后移选中页", "后移", Symbol.Forward, () => MoveOrganizerSelection(1));
        _organizerRotateLeft = OrganizerCommand("逆时针旋转选中页", "左转", Symbol.Rotate, () => RotateOrganizerSelection(-90));
        _organizerRotateLeft.SetContent(new SymbolIcon(Symbol.Rotate) { RenderTransformOrigin = new(.5, .5), RenderTransform = new ScaleTransform { ScaleX = -1 } });
        _organizerRotateRight = OrganizerCommand("顺时针旋转选中页", "右转", Symbol.Rotate, () => RotateOrganizerSelection(90));
        _organizerDelete = OrganizerCommand("删除选中页", "删除", Symbol.Delete, DeleteOrganizerSelection);
        _organizerUndo = OrganizerCommand("撤销页面更改", "撤销", Symbol.Undo, () => Controller.Undo());
        _organizerRedo = OrganizerCommand("重做页面更改", "重做", Symbol.Redo, () => Controller.Redo());
        foreach (var button in new[] { _organizerOpenPage, _organizerPrevious, _organizerNext, _organizerRotateLeft,
            _organizerRotateRight, _organizerDelete, _organizerUndo, _organizerRedo })
            actions.Children.Add(button);
        Grid.SetColumn(actions, 1); controls.Children.Add(actions);
        _organizer.Children.Add(controls);
        Grid.SetRow(_pageGrid, 1); _organizer.Children.Add(_pageGrid);
        Grid.SetRow(_organizer, 1); _root.Children.Add(_organizer);

        AutomationProperties.SetName(_pageGrid, "PDF 页面管理网格");
        AutomationProperties.SetName(_organizerCount, "PDF 页面选择计数");
        _pageGrid.Margin = new Thickness(12, 0, 12, 8);
        ScrollViewer.SetHorizontalScrollMode(_pageGrid, ScrollMode.Disabled);
        ScrollViewer.SetHorizontalScrollBarVisibility(_pageGrid, ScrollBarVisibility.Disabled);
        ScrollViewer.SetVerticalScrollBarVisibility(_pageGrid, ScrollBarVisibility.Auto);
        _pageGrid.ItemsPanel = (ItemsPanelTemplate)XamlReader.Load("""
            <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <ItemsWrapGrid Orientation="Horizontal" ItemWidth="172" ItemHeight="214" CacheLength="0.35" />
            </ItemsPanelTemplate>
            """);
        _pageGrid.ItemTemplate = (DataTemplate)XamlReader.Load("""
            <DataTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                <Grid Width="148" Height="196" RowSpacing="8">
                    <Grid.RowDefinitions>
                        <RowDefinition Height="*" />
                        <RowDefinition Height="Auto" />
                    </Grid.RowDefinitions>
                    <Border HorizontalAlignment="Center" VerticalAlignment="Center" Width="{Binding Width}" Height="{Binding Height}"
                            Background="White" BorderBrush="{Binding FrameBrush}" BorderThickness="1" CornerRadius="2">
                        <Image Source="{Binding Preview}" Stretch="Uniform" />
                    </Border>
                    <TextBlock Grid.Row="1" Text="{Binding PageNumber}" HorizontalAlignment="Center" FontSize="13" />
                </Grid>
            </DataTemplate>
            """);
        _pageGrid.ItemContainerStyle = new Style(typeof(GridViewItem));
        _pageGrid.ItemContainerStyle.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Center));
        _pageGrid.ItemContainerStyle.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(4)));
        _pageGrid.ItemsSource = _gridItems;
        _pageGrid.CanDragItems = _pageGrid.CanReorderItems = _pageGrid.AllowDrop = CanEdit;
        _pageGrid.ContainerContentChanging += OrganizerContainerChanged;
        _pageGrid.SelectionChanged += (_, _) => { if (!_syncingPageGrid) UpdateOrganizerCommands(); };
        _pageGrid.DoubleTapped += (_, args) =>
        {
            for (var element = args.OriginalSource as DependencyObject; element is not null; element = VisualTreeHelper.GetParent(element))
            {
                if (element is not GridViewItem container) continue;
                if (_pageGrid.ItemFromContainer(container) is ThumbnailItem item) OpenSourcePage(item.Index);
                break;
            }
        };
        _pageGrid.DragItemsStarting += (_, args) =>
        {
            if (!CanOrganizePages || args.Items.Count != 1) { args.Cancel = true; return; }
            args.Data.RequestedOperation = DataPackageOperation.Move;
        };
        _pageGrid.DragItemsCompleted += (_, args) =>
        {
            if (!_organizerOpen || !CanOrganizePages || args.DropResult != DataPackageOperation.Move)
            {
                if (_organizerOpen) SyncOrganizerItems(SelectedSourcePages(), scroll: false);
                return;
            }
            var next = _gridItems.Select(item => item.Index).ToArray();
            if (next.Length == _pageOrder.Length && next.Distinct().Count() == next.Length)
                ChangePageOrder(next, "移动 PDF 页面");
        };
        ConfigureOrganizerThumbnails();
        _inkHistory.Changed += UpdateOrganizerCommands;
    }

    private TouchIconButton OrganizerCommand(string automationName, string label, Symbol symbol, Action action)
    {
        var button = Command(automationName, symbol, action);
        button.Label = label;
        button.ShowLabel = true;
        button.Width = button.MinWidth = 42;
        button.Height = 44;
        button.Padding = new Thickness(2);
        button.IconAreaHeight = 22;
        button.LabelFontSize = 11;
        return button;
    }

    private bool CanOrganizePages => !_closed && !_saving && !_closing && CanEdit
        && _spacingDrag is null && Controller is { HasActiveInteraction: false };

    internal void SetOrganizerOpen(bool open)
    {
        if (_closed || _organizerOpen == open || Controller is null || Controller.HasActiveInteraction || _spacingDrag is not null) return;
        StopScrollMotion();
        _organizerOpen = open;
        _organizerToggle.IsSelected = open;
        _readingCommands.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        _split.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        _organizer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        UpdateToolbarLayout();
        if (open)
        {
            NormalizeCrossPageInk();
            if (_floatingSearch.Visibility == Visibility.Visible) CloseSearch();
            SetSpacingTool(SpacingTool.None);
            Controller.ClearSelection();
            Controller.EndPlayback();
            _renderCancellation?.Cancel();
            _detailCancellation?.Cancel();
            _detailTimer.Stop();
            SuspendThumbnails();
            RefreshOrganizerItems([_pageIndex]);
            _pageGrid.Focus(FocusState.Programmatic);
        }
        else
        {
            SuspendOrganizerThumbnails(clearCache: false);
            if (_organizerNeedsPosition)
            {
                _organizerNeedsPosition = false;
                _split.UpdateLayout();
                PositionPage(DocumentView);
                UpdateScrollbar();
            }
            RefreshThumbnails();
            QueuePageRendering();
            _detailTimer.Start();
            _ink.Focus(FocusState.Programmatic);
        }
        UpdateOrganizerCommands();
    }

    private int[] SelectedSourcePages() => _pageGrid.SelectedItems.OfType<ThumbnailItem>().Select(item => item.Index)
        .Where(page => _layout.PositionOf(page) >= 0).OrderBy(_layout.PositionOf).ToArray();

    private void OpenOrganizerSelection()
    {
        if (_pageGrid.SelectedItems.Count == 1 && _pageGrid.SelectedItems[0] is ThumbnailItem item) OpenSourcePage(item.Index);
    }

    private void UpdateOrganizerCommands()
    {
        if (!_organizerOpen || _syncingPageGrid) return;
        int count = _pageGrid.SelectedItems.Count;
        _organizerCount.Text = count == 0 ? $"共 {_layout.Count} 页" : $"已选 {count} 页 · 共 {_layout.Count} 页";
        _organizerOpenPage.IsEnabled = count == 1;
        bool editable = count > 0 && CanOrganizePages;
        _organizerRotateLeft.IsEnabled = _organizerRotateRight.IsEnabled = editable;
        _organizerDelete.IsEnabled = editable && count < _layout.Count;
        _organizerPrevious.IsEnabled = editable && !_pageGrid.SelectedItems.Contains(_organizerItems[_pageOrder[0]]);
        _organizerNext.IsEnabled = editable && !_pageGrid.SelectedItems.Contains(_organizerItems[_pageOrder[^1]]);
        _organizerUndo.IsEnabled = CanEdit && !_saving && _inkHistory.UndoCount > 0;
        _organizerRedo.IsEnabled = CanEdit && !_saving && _inkHistory.RedoCount > 0;
    }

    private void RefreshOrganizerItems(int[] selection)
    {
        _organizerItems ??= Enumerable.Range(0, _file.Pages.Count).Select(page =>
            new ThumbnailItem(page, ThumbnailGeometry(page), 1, _thumbnailBorder, maxWidth: 142, maxHeight: 170)).ToArray();
        SyncOrganizerItems(selection, scroll: true);
    }

    private void SyncOrganizerItems(int[] selection, bool scroll)
    {
        _syncingPageGrid = true;
        try
        {
            for (int i = 0; i < _pageOrder.Length; i++) _organizerItems[_pageOrder[i]].SetPosition(i);
            if (scroll) _pageGrid.SelectedItems.Clear();
            if (!_gridItems.Select(item => item.Index).SequenceEqual(_pageOrder))
            {
                SuspendOrganizerThumbnails(clearCache: false);
                _gridItems = new ObservableCollection<ThumbnailItem>(_pageOrder.Select(page => _organizerItems[page]));
                _pageGrid.ItemsSource = _gridItems;
            }
            foreach (int page in selection)
            {
                if (_layout.PositionOf(page) < 0) continue;
                var item = _organizerItems[page];
                if (!_pageGrid.SelectedItems.Contains(item)) _pageGrid.SelectedItems.Add(item);
            }
            if (scroll)
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_organizerOpen && _layout.PositionOf(_pageIndex) >= 0)
                        _pageGrid.ScrollIntoView(_pageGrid.SelectedItems.OfType<ThumbnailItem>().FirstOrDefault() ?? _organizerItems[_pageIndex]);
                });
        }
        finally { _syncingPageGrid = false; }
        QueueOrganizerThumbnails();
        UpdateOrganizerCommands();
    }

    private void MoveOrganizerSelection(int direction)
    {
        if (!CanOrganizePages) return;
        var selected = SelectedSourcePages().ToHashSet();
        if (selected.Count == 0 || selected.Contains(direction < 0 ? _pageOrder[0] : _pageOrder[^1])) return;
        var next = _pageOrder.ToArray();
        for (int i = direction < 0 ? 1 : next.Length - 2; i >= 0 && i < next.Length; i -= direction)
            if (selected.Contains(next[i])) (next[i], next[i + direction]) = (next[i + direction], next[i]);
        ChangePageOrder(next, "移动 PDF 页面");
    }

    private void RotateOrganizerSelection(int degrees) => RotatePages(SelectedSourcePages(), degrees);

    internal void DeleteOrganizerSelection()
    {
        if (!CanOrganizePages) return;
        var selected = SelectedSourcePages().ToHashSet();
        if (selected.Count == 0 || selected.Count >= _pageOrder.Length) return;
        ChangePageOrder(_pageOrder.Where(page => !selected.Contains(page)).ToArray(), "删除 PDF 页面");
    }

    internal bool ChangePageOrder(int[] order, string operation)
    {
        if (!CanOrganizePages || order.Length == 0 || order.Distinct().Count() != order.Length
            || order.Any(page => _layout.PositionOf(page) < 0) || _pageOrder.SequenceEqual(order)) return false;
        NormalizeCrossPageInk();
        var before = _pageOrder;
        var selection = SelectedSourcePages();
        int beforePage = _pageIndex;
        var after = order.ToArray();
        int afterPage = after.Contains(beforePage) ? beforePage : after[Math.Min(PageIndex, after.Length - 1)];
        ApplyPageOrder(after, afterPage, selection);
        _inkHistory.Push(new DelegateAction(operation, () => ApplyPageOrder(after, afterPage, selection),
            () => ApplyPageOrder(before, beforePage, selection)));
        return true;
    }

    private void ApplyPageOrder(int[] order, int page, int[] selection)
    {
        var document = DocumentView;
        _pageOrder = order;
        RebuildLayout(document, page);
        _pageChanging = true;
        try
        {
            for (int i = 0; i < order.Length; i++) _sidebarItems[order[i]].SetPosition(i);
            _pages.ItemsSource = order.Select(index => _sidebarItems[index]).ToArray();
            _pages.SelectedIndex = PageIndex;
            RefreshBookmarks();
            if (_hits.Count > 0 || _searching) ClearSearch();
        }
        finally { _pageChanging = false; }
        _pageCount.Text = "/ " + order.Length;
        _pageNumber.Text = (PageIndex + 1).ToString();
        if (_organizerOpen) RefreshOrganizerItems(selection.Where(page => _layout.PositionOf(page) >= 0).DefaultIfEmpty(_pageIndex).ToArray());
        _version++;
        UpdateTitle();
        ReadingProgressChanged?.Invoke();
    }
}
