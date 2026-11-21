using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Ink;

namespace InkCanvas.App.Diagnostics;

internal static class BoardFeatureRegression
{
    public static bool Run(List<object> checks)
    {
        bool passed = true;
        var board = new BoardService();
        var first = board.Current;
        first.CurrentAttributes.Color = InkColor.Opaque(220, 38, 38);
        first.CurrentAttributes.BaseWidth = 7;
        first.CurrentAttributes.IsHighlighter = true;
        first.EraserMode = InkEraserMode.Lasso;
        first.Tool = InkTool.Pen;
        first.Document.Add(Stroke(new(20, 20), new(80, 80)));
        board.InsertPageAfterCurrent();
        var second = board.Current;
        bool stateCarried = second.CurrentAttributes.Color == first.CurrentAttributes.Color
            && Math.Abs(second.CurrentAttributes.BaseWidth - 7) < .01f
            && second.CurrentAttributes.IsHighlighter && second.EraserMode == InkEraserMode.Lasso;
        board.PreviousPage();
        bool navigation = board.CurrentIndex == 0 && board.Current.Document.Count == 1 && board.PageCount == 2;
        board.GoTo(1);
        var image = Image(100, 100); var image2 = Image(500, 100);
        board.AddImage(image); board.AddImage(image2);
        var media = new BoardMediaAsset { Data = new byte[] { 1 }, Left = 150, Top = 120, Width = 60, Height = 60 };
        var media2 = new BoardMediaAsset { Data = new byte[] { 2 }, Left = 240, Top = 120, Width = 60, Height = 60 };
        board.AddMedia(media); board.AddMedia(media2);
        var source = Stroke(new(120, 120), new(180, 180));
        source.Attributes.IsHighlighter = true; source.Attributes.HighlighterAlpha = 180;
        second.Document.Add(source);
        second.Tool = InkTool.Select;
        var elements = new ISelectionElement[] { new BoardSelectionElement(board, image), new BoardSelectionElement(board, image2),
            new BoardSelectionElement(board, media), new BoardSelectionElement(board, media2) };
        second.GetSelectableElements = () => elements;
        second.Selection.Add(source); second.SelectedElements.AddRange(elements);
        bool cloned = board.CloneSelectionToNewPage();
        bool cloneContent = cloned && board.Current.Document.Count == 1 && board.CurrentImages.Count == 2
            && board.CurrentMedia.Count == 2 && board.CurrentImages[0].Id != image.Id && board.CurrentMedia[1].Id != media2.Id
            && ReferenceEquals(board.CurrentImages[0].Data, image.Data);
        board.Current.Undo();
        bool cloneUndo = board.Current.Document.Count == 0 && board.CurrentImages.Count == 0 && board.CurrentMedia.Count == 0;
        board.Current.Redo();
        bool cloneRedo = board.Current.Document.Count == 1 && board.CurrentImages.Count == 2 && board.CurrentMedia.Count == 2;
        board.ClearCurrentPage();
        bool clear = board.Current.Document.Count == 0 && board.CurrentImages.Count == 0 && board.CurrentMedia.Count == 0;
        board.Current.Undo();
        bool clearUndo = board.Current.Document.Count == 1 && board.CurrentImages.Count == 2 && board.CurrentMedia.Count == 2;
        passed &= stateCarried && navigation && cloneContent && cloneUndo && cloneRedo && clear && clearUndo;
        checks.Add(new { check = "board navigation, multi-object clone and atomic clear", stateCarried, navigation,
            cloneContent, cloneUndo, cloneRedo, clear, clearUndo });

        string path = Path.Combine(Path.GetTempPath(), $"inkcanvas-board-{Guid.NewGuid():N}.icink");
        try
        {
            board.SaveBoard(path);
            var loaded = new BoardService(); loaded.LoadBoard(path);
            loaded.GoTo(board.CurrentIndex);
            bool persisted = loaded.PageCount == board.PageCount && loaded.CurrentImages.Count == board.CurrentImages.Count
                && loaded.CurrentMedia.Count == board.CurrentMedia.Count && loaded.Current.Document.Count == board.Current.Document.Count
                && loaded.Current.Document.Strokes[0].Attributes.HighlighterAlpha == 180;
            passed &= persisted;
            checks.Add(new { check = "board package round-trips media-only pages and ink", persisted });
            foreach (var page in loaded.Pages) page.Dispose();
            var mediaOnly = new BoardService();
            mediaOnly.AddMedia(media.Copy()); mediaOnly.SavePage(path);
            var reopened = new BoardService(); reopened.LoadPage(path);
            bool mediaOnlySaved = reopened.Current.Document.Count == 0 && reopened.CurrentMedia.Count == 1
                && reopened.CurrentMedia[0].Data.SequenceEqual(media.Data);
            passed &= mediaOnlySaved;
            checks.Add(new { check = "video-only board page saves without dropping media", mediaOnlySaved });
            foreach (var page in mediaOnly.Pages.Concat(reopened.Pages)) page.Dispose();
        }
        catch
        {
            passed = false; checks.Add(new { check = "board package round-trips media-only pages and ink", persisted = false });
        }
        finally { try { File.Delete(path); } catch { } }
        foreach (var page in board.Pages) page.Dispose();
        return passed;
    }

    private static BoardImageAsset Image(float left, float top)
        => new() { Data = new byte[] { 1 }, Left = left, Top = top, Width = 60, Height = 60 };
    private static InkStroke Stroke(params Vector2[] points)
    {
        var stroke = new InkStroke(new StrokeAttributes { TaperKind = TaperKind.None });
        foreach (var p in points) stroke.AddPoint(p.X, p.Y);
        stroke.Complete(); return stroke;
    }
}
