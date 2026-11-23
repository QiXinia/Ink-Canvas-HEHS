using System.Numerics;
using InkCanvas.Pdf;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace InkCanvas.Modules.Pdf;

public sealed partial class PdfWindow
{
    private readonly int[] _pageRotations;
    private bool _doublePages, _separateCover;
    private ToggleMenuFlyoutItem _singleLayout, _doubleLayout, _coverLayout;
    internal bool DoublePages => _doublePages;
    internal bool SeparateCover => _separateCover;
    internal PdfContinuousLayout PageLayout => _layout;

    private void NavigatePage(int direction) => GoToPage(_layout.AdjacentPage(_pageIndex, direction));

    private void PositionPage(Matrix3x2 document)
    {
        var bounds = _layout.SpreadBounds(_pageIndex);
        float scale = MathF.Sqrt(MathF.Abs(document.GetDeterminant()));
        if (bounds.Width * scale <= _viewport.ActualWidth)
            document.M31 = ((float)_viewport.ActualWidth - bounds.Width * scale) / 2 - bounds.Left * scale;
        document.M32 = 16 - bounds.Top * scale;
        Controller.ViewTransform = _layout.PageView(_pageIndex, document);
        ClampDocumentView();
        Controller.NotifyElementsChanged();
    }

    internal void SetPageLayout(bool doublePages, bool separateCover)
    {
        if (Controller.HasActiveInteraction)
        {
            UpdateLayoutMenu();
            return;
        }
        if (_doublePages == doublePages && _separateCover == separateCover)
        {
            UpdateLayoutMenu();
            return;
        }
        NormalizeCrossPageInk();
        var document = DocumentView;
        _doublePages = doublePages;
        _separateCover = separateCover;
        RebuildLayout(document);
        UpdateLayoutMenu();
    }

    private void UpdateLayoutMenu()
    {
        _singleLayout.IsChecked = !_doublePages;
        _doubleLayout.IsChecked = _doublePages;
        _coverLayout.IsChecked = _separateCover;
        _coverLayout.IsEnabled = _doublePages;
    }

    internal void RotatePage(int degrees)
    {
        if (Controller.HasActiveInteraction || degrees % 90 != 0) return;
        NormalizeCrossPageInk();
        var document = DocumentView;
        _pageRotations[_pageIndex] = (_pageRotations[_pageIndex] + degrees + 360) % 360;
        if (!_file.ReadOnly) { _version++; UpdateTitle(); }
        RebuildLayout(document);
    }

    private void RebuildLayout(Matrix3x2 document)
    {
        StopScrollMotion();
        ReleaseNavigationPicture();
        Controller.ClearSelection();
        _layout = new PdfContinuousLayout(_file.Pages, doublePages: _doublePages, separateCover: _separateCover, rotations: _pageRotations);
        _generation++;
        _detailCancellation?.Cancel();
        _detail?.Dispose(); _detail = null;
        _pageChanging = true;
        try { PositionPage(document); }
        finally { _pageChanging = false; }
        _lastView = Controller.ViewTransform;
        _navigationPictureDirty = true;
        _paper.Invalidate();
        UpdateScrollbar();
        QueuePageRendering();
        foreach (var item in ((System.Collections.IEnumerable)_pages.ItemsSource).Cast<ThumbnailItem>())
            item.SetGeometry(_file.Pages[item.Index] with { Rotation = _file.Pages[item.Index].Rotation + _pageRotations[item.Index] });
        RefreshThumbnails();
    }

    private MenuFlyout CreateLayoutMenu()
    {
        var menu = new MenuFlyout();
        _singleLayout = new ToggleMenuFlyoutItem { Text = "单页连续", IsChecked = true };
        _doubleLayout = new ToggleMenuFlyoutItem { Text = "双页连续" };
        _coverLayout = new ToggleMenuFlyoutItem { Text = "单独显示封面", IsEnabled = false };
        _singleLayout.Click += (_, _) => SetPageLayout(false, _separateCover);
        _doubleLayout.Click += (_, _) => SetPageLayout(true, _separateCover);
        _coverLayout.Click += (_, _) => SetPageLayout(_doublePages, _coverLayout.IsChecked);
        menu.Items.Add(_singleLayout); menu.Items.Add(_doubleLayout); menu.Items.Add(_coverLayout);
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuCommand("顺时针旋转当前页", Symbol.Rotate, () => RotatePage(90)));
        menu.Items.Add(MenuCommand("逆时针旋转当前页", Symbol.Rotate, () => RotatePage(-90)));
        return menu;
    }
}
