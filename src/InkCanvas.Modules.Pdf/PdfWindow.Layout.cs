using System.Numerics;
using InkCanvas.Core.History;
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

    private void NavigatePage(int direction) => OpenSourcePage(_layout.AdjacentPage(_pageIndex, direction));

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
        if (Controller.HasActiveInteraction || _spacingDrag is not null)
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

    internal void RotatePage(int degrees) => RotatePages([_pageIndex], degrees);

    private void RotatePages(int[] pages, int degrees)
    {
        if (_saving || Controller.HasActiveInteraction || _spacingDrag is not null || degrees % 90 != 0
            || degrees % 360 == 0 || pages.Length == 0) return;
        NormalizeCrossPageInk();
        var before = pages.Select(page => _pageRotations[page]).ToArray();
        var after = before.Select(turn => (turn + degrees % 360 + 360) % 360).ToArray();
        Apply(after);
        if (CanEdit) _inkHistory.Push(new DelegateAction("旋转 PDF 页面", () => Apply(after), () => Apply(before)));

        void Apply(int[] rotations)
        {
            var document = DocumentView;
            for (int i = 0; i < pages.Length; i++)
            {
                _pageRotations[pages[i]] = rotations[i];
                UpdateThumbnailGeometry(pages[i]);
            }
            RebuildLayout(document);
            if (CanEdit) { _version++; UpdateTitle(); }
        }
    }

    private void RebuildLayout(Matrix3x2 document, int? activePage = null)
    {
        StopScrollMotion();
        ReleaseNavigationPicture();
        Controller.ClearSelection();
        ClearSpacingSelection();
        _layout = CreatePageLayout();
        if (activePage is { } page && page != _pageIndex) ActivatePage(page, preserveView: true, document);
        _generation++;
        _detailCancellation?.Cancel();
        _detail?.Dispose(); _detail = null;
        _pageChanging = true;
        try
        {
            if (_organizerOpen)
            {
                Controller.ViewTransform = _layout.PageView(_pageIndex, document);
                _organizerNeedsPosition = true;
            }
            else PositionPage(document);
        }
        finally { _pageChanging = false; }
        _lastView = Controller.ViewTransform;
        _navigationPictureDirty = true;
        _paper.Invalidate();
        UpdateScrollbar();
        QueuePageRendering();
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
        menu.Items.Add(MenuCommand("页面管理", Symbol.ViewAll, () => SetOrganizerOpen(true)));
        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuCommand("顺时针旋转当前页", Symbol.Rotate, () => RotatePage(90)));
        menu.Items.Add(MenuCommand("逆时针旋转当前页", Symbol.Rotate, () => RotatePage(-90)));
        return menu;
    }
}
