using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Features.Board;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace InkCanvas.App.Views;

public sealed partial class BoardWindow
{
    private bool _pastingSideBoard;

    private void PlaceSideBoard(Window owner)
    {
        var screen = Platform.System.ScreenService.FromWindow(WindowChrome.GetHwnd(owner));
        var work = screen.WorkArea;
        int margin = (int)Math.Round(12 * screen.DpiScale);
        int width = Math.Min(Math.Max(1, work.Width - margin * 2), (int)Math.Round(580 * screen.DpiScale));
        int height = Math.Min(Math.Max(1, work.Height - margin * 2), (int)Math.Round(700 * screen.DpiScale));
        WindowChrome.Place(this, work.X + work.Width - width - margin, work.Y + margin, width, height);
    }

    private void OnSideBoardClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_archiveCloseApproved) return;
        args.Cancel = true;
        ExitBoard();
    }

    internal void HideSideBoardWindow()
    {
        DismissBoardFlyout();
        foreach (var panel in _toolPanels.Values.ToArray()) panel.Close();
        SetPagePaneOpen(false);
        FinishSelectionPointers();
        CurrentController.EndPlayback();
        _media.PausePlayers();
        AppWindow.Hide();
    }

    private Flyout BuildSideBoardFlyout()
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.Top };
        StyleBoardFlyout(flyout);
        var panel = new StackPanel { Spacing = 4, Padding = new Thickness(8) };
        panel.Children.Add(new PopupHeader("板中板", _boardTheme));
        var shell = (ShellController)_shell;
        Add(Glyphs.Import, "贴到主白板", async () => await PasteSideBoardAsync(shell.MainBoardWindow));
        if (shell.AiAssistantAvailable)
            Add(Glyphs.Assistant, "AI 助手", shell.OpenAiAssistant);
        Add(Glyphs.Pages, "页面管理", () => SetPagePaneOpen(true));
        Add(GlyphCanvas, "画布纸型", () => ShowBoardFlyout(BuildCanvasFlyout(), _moreCell.Button));
        Add(GlyphImage, "插入图片", async () => await InsertImageAsync());
        Add(GlyphClear, "清空当前页", () => ClearCurrentPage());
        flyout.Content = panel;
        return flyout;

        void Add(string glyph, string label, Action action) =>
            panel.Children.Add(MakeExtraButton(glyph, label, () => { HideBoardFlyout(flyout); action(); }));
    }

    private async Task PasteSideBoardAsync(BoardWindow main)
    {
        if (_pastingSideBoard || main is null || main.IsClosed) return;
        _pastingSideBoard = true;
        var source = CurrentController;
        var target = main.CurrentController;
        var mainDocument = main._board.DocumentId;
        bool IsCurrent() => !_closed && AppWindow.IsVisible && !main.IsClosed && main.AppWindow.IsVisible
            && ReferenceEquals(source, CurrentController) && ReferenceEquals(target, main.CurrentController)
            && mainDocument == main._board.DocumentId;
        try
        {
            if (!await ImageImport.WaitForCanvasAsync(source, IsCurrent, default) || !IsCurrent()) return;
            source.FinishDotAnimations();
            var (png, bounds) = await BoardPreview.RenderContentAsync(_board, _board.CurrentIndex);
            if (!IsCurrent()) return;
            if (png is null) { await ShowMessageAsync("板中板当前页没有可以贴回的内容。"); return; }
            if (!await ImageImport.WaitForCanvasAsync(target, IsCurrent, default) || !IsCurrent()) return;
            var image = BoardPasteLayout.CreateImageAsset("板中板.png", png, bounds.Width, bounds.Height,
                (float)main._surface.ActualWidth, (float)main._surface.ActualHeight, target.ViewTransform);
            if (image is not null) main.InsertImage(image);
        }
        catch (Exception ex)
        {
            if (IsCurrent()) await ShowMessageAsync($"贴到主白板失败：{ex.Message}");
        }
        finally { _pastingSideBoard = false; }
    }
}
