using System.Numerics;
using InkCanvas.Core.AiAssistant;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Features.Board.AiObjects;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>
/// Native AI board objects: one undo step per write, grouped selection, editable updates,
/// removal order, v4 persistence round-trip and legacy files without the new fields.
/// </summary>
public sealed class BoardAiObjectTests
{
    private static string TempFile(string name)
        => Path.Combine(Path.GetTempPath(), $"inkcanvas-ai-native-{Guid.NewGuid():N}-{name}");

    private static BoardTextObject Text(string content, float left, float top, string type = "text") => new()
    {
        Type = type,
        Content = content,
        Title = type == "text" ? "" : "标题",
        FontFamily = "Microsoft YaHei UI",
        FontSize = 32,
        Color = "#FF202024",
        LineHeight = 1.4,
        Align = "left",
        Left = left,
        Top = top,
        Width = 320,
        Height = 140,
        Source = "ai",
        Instruction = "整理当前页板书",
        CreatedAt = 1730000000000,
    };

    private static BoardFormulaObject Formula(string latex, float left, float top) => new()
    {
        Latex = latex,
        FontFamily = "Cambria Math",
        FontSize = 30,
        Color = "#FF202024",
        Left = left,
        Top = top,
        Width = 220,
        Height = 90,
        Source = "ai",
        Instruction = "整理当前页板书",
        CreatedAt = 1730000000000,
    };

    private static BoardService Store(InkController controller)
    {
        var store = new BoardService(controller);
        controller.GetSelectableElements = store.GetSelectionElements;
        return store;
    }

    private static OverlayPointer Pointer(Vector2 point, bool control = false)
        => new(451, point.X, point.Y, .5f, 0, 0, OverlayPointerKind.Mouse, 0, Control: control);

    [Fact]
    public void AddAiObjectsAtomic_InsertsAllAndUndoesAsOneStep()
    {
        var board = new BoardService();
        var first = Text("一元二次方程", 10, 20);
        var second = Text("判别式", 10, 280, "wordList");
        var formula = Formula("\\frac{1}{2}x^2", 10, 160);

        IReadOnlyList<Guid> ids = board.AddAiObjectsAtomic(
            new[] { first, second }, new[] { formula }, "AI 板书", "group-1");

        Assert.Equal(new[] { first.Id, second.Id, formula.Id }, ids);
        Assert.Equal(new[] { "一元二次方程", "判别式" }, board.CurrentTextObjects.Select(text => text.Content));
        Assert.Same(formula, Assert.Single(board.CurrentFormulaObjects));
        Assert.All(board.CurrentTextObjects.Concat<ISelectionElement>(board.CurrentFormulaObjects),
            element => Assert.Equal("group-1", Assert.IsAssignableFrom<IGroupedSelectionElement>(element).SelectionGroupId));
        Assert.Equal(0, first.LayerOrder);
        Assert.Equal(1, second.LayerOrder);
        Assert.Equal(2, formula.LayerOrder);
        Assert.Equal(1, board.Current.History.UndoCount);

        board.Current.Undo();
        Assert.Empty(board.CurrentTextObjects);
        Assert.Empty(board.CurrentFormulaObjects);
        Assert.Equal(1, board.Current.History.RedoCount);

        board.Current.Redo();
        Assert.Equal(new[] { first.Id, second.Id }, board.CurrentTextObjects.Select(text => text.Id));
        Assert.Equal(formula.Id, Assert.Single(board.CurrentFormulaObjects).Id);
        Assert.Equal(1, board.Current.History.UndoCount);
    }

    [Fact]
    public void AddAiObjectsAtomic_SkipsInvalidObjectsAndEmptyInputIsNoOp()
    {
        var board = new BoardService();
        var valid = Text("有效文本", 0, 0);
        var empty = Text("   ", 0, 0);
        var invalidFormula = Formula("", 0, 0);

        IReadOnlyList<Guid> ids = board.AddAiObjectsAtomic(
            new[] { empty, valid }, new[] { invalidFormula }, "AI 板书");

        Assert.Equal(new[] { valid.Id }, ids);
        Assert.Single(board.CurrentTextObjects);
        Assert.Empty(board.CurrentFormulaObjects);
        Assert.Equal(1, board.Current.History.UndoCount);

        Assert.Empty(board.AddAiObjectsAtomic(null, null, "AI 板书"));
        Assert.Equal(1, board.Current.History.UndoCount);
    }

    [Fact]
    public void GroupSelectionMovesAllMembersAndUngroupIsOneUndoStep()
    {
        using var controller = new InkController();
        var board = Store(controller);
        var text = Text("总结", 100, 100);
        var formula = Formula("x^2", 420, 100);
        board.AddAiObjectsAtomic(new[] { text }, new[] { formula }, "AI 板书", "group-2");

        controller.SelectElement(text);
        Assert.Equal(2, controller.SelectedElements.Count);
        controller.History.Clear();

        var center = new Vector2(100 + text.Width / 2f, 100 + text.Height / 2f);
        controller.OnPointerDown(Pointer(center));
        controller.OnPointerMove(Pointer(center + new Vector2(60, 40)));
        controller.OnPointerUp(Pointer(center + new Vector2(60, 40)));

        Assert.Equal(new Vector2(60, 40), BoardElementMath.Read(text.TransformMatrix).Translation);
        Assert.Equal(new Vector2(60, 40), BoardElementMath.Read(formula.TransformMatrix).Translation);
        Assert.Equal(1, controller.History.UndoCount);

        controller.Undo();
        Assert.Equal(Matrix3x2.Identity, BoardElementMath.Read(text.TransformMatrix));
        Assert.Equal(Matrix3x2.Identity, BoardElementMath.Read(formula.TransformMatrix));

        controller.ClearSelection();
        Assert.Equal(2, board.UngroupAiObjects("group-2"));
        Assert.Equal("", text.GroupId);
        Assert.Equal("", formula.GroupId);
        Assert.Equal(1, board.Current.History.UndoCount);

        controller.SelectElement(text);
        Assert.Single(controller.SelectedElements);

        board.Current.Undo();
        Assert.Equal("group-2", text.GroupId);
        controller.ClearSelection();
        controller.SelectElement(text);
        Assert.Equal(2, controller.SelectedElements.Count);
    }

    [Fact]
    public void UpdateAiObject_UndoesContentStyleAndGeometryTogether()
    {
        var board = new BoardService();
        board.SetBackground(BoardBackground.White);
        var text = Text("原内容", 10, 20);
        var formula = Formula("x^2", 10, 200);
        board.AddAiObjectsAtomic(new[] { text }, new[] { formula }, "AI 板书");
        board.Current.History.Clear();

        Assert.True(board.UpdateAiObject(text, target =>
        {
            target.Content = "新内容";
            target.Title = "新标题";
            target.FontSize = 48;
            target.Color = "#FF112233";
            target.Align = "center";
            target.Columns = 3;
            target.Left = 120;
            target.Top = 160;
        }));
        Assert.Equal("新内容", text.Content);
        Assert.Equal(48, text.FontSize);
        Assert.Equal(1, board.Current.History.UndoCount);

        board.Current.Undo();
        Assert.Equal("原内容", text.Content);
        Assert.Equal("", text.Title);
        Assert.Equal(32, text.FontSize);
        Assert.Equal("#FF202024", text.Color);
        Assert.Equal("left", text.Align);
        Assert.Equal(1, text.Columns);
        Assert.Equal(10, text.Left);
        Assert.Equal(20, text.Top);

        board.Current.Redo();
        Assert.Equal("新内容", text.Content);

        board.Current.Undo();
        Assert.True(board.UpdateAiObject(formula, target =>
        {
            target.Latex = "\\frac{1}{2}";
            target.FontSize = 42;
            target.Width = 300;
        }));
        Assert.Equal("\\frac{1}{2}", formula.Latex);
        board.Current.Undo();
        Assert.Equal("x^2", formula.Latex);

        int undoCount = board.Current.History.UndoCount;
        Assert.False(board.UpdateAiObject(text, target => target.Content = ""));
        Assert.False(board.UpdateAiObject(formula, target => target.Latex = ""));
        Assert.Equal(undoCount, board.Current.History.UndoCount);
        Assert.Equal("原内容", text.Content);
        Assert.Equal("x^2", formula.Latex);
    }

    [Fact]
    public void ReplaceStrokesWithAiObjectsAtomic_IsOneUndoStep()
    {
        var board = new BoardService();
        InkStroke first = Stroke(120, 120);
        InkStroke second = Stroke(220, 180);
        board.Current.Document.Add(first);
        board.Current.Document.Add(second);

        var text = Text("替换文本", 40, 40);
        var formula = Formula("a^2+b^2=c^2", 40, 180);
        IReadOnlyList<Guid> ids = board.ReplaceStrokesWithAiObjectsAtomic(
            new[] { first, second }, new[] { text }, new[] { formula }, "AI 板书替换", "replace-group");

        Assert.Equal(2, ids.Count);
        Assert.Empty(board.Current.Document.Strokes);
        Assert.Single(board.CurrentTextObjects);
        Assert.Single(board.CurrentFormulaObjects);
        Assert.Empty(board.CurrentImages);
        Assert.Equal("replace-group", text.GroupId);
        Assert.Equal(1, board.Current.History.UndoCount);

        board.Current.Undo();
        Assert.Equal(2, board.Current.Document.Count);
        Assert.Empty(board.CurrentTextObjects);
        Assert.Empty(board.CurrentFormulaObjects);

        board.Current.Redo();
        Assert.Empty(board.Current.Document.Strokes);
        Assert.Single(board.CurrentTextObjects);
        Assert.Single(board.CurrentFormulaObjects);
    }

    private static InkStroke Stroke(float x, float y)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 2, TaperKind = TaperKind.None });
        stroke.AddPoint(x, y);
        stroke.AddPoint(x + 40, y + 20);
        stroke.Complete();
        return stroke;
    }

    [Fact]
    public void ApplyAiObjectEdit_UpdatesSelectedGroupAsOneUndo()
    {
        var board = new BoardService();
        var text = Text("组内文本", 10, 10);
        var formula = Formula("x^2", 10, 200);
        board.AddAiObjectsAtomic(new[] { text }, new[] { formula }, "AI 板书", "edit-group");
        board.Current.History.Clear();

        var edit = new BoardPlanEditPolicy.BoardObjectEdit
        {
            FontScale = 1.2,
            Columns = 2,
            Align = "center",
        };
        Assert.True(board.ApplyAiObjectEdit(new ISelectionElement[] { text, formula }, edit, "连续修改"));

        Assert.Equal(38.4, text.FontSize, 3);
        Assert.Equal(2, text.Columns);
        Assert.Equal("center", text.Align);
        Assert.Equal(36, formula.FontSize, 3);
        Assert.Single(board.CurrentTextObjects);
        Assert.Single(board.CurrentFormulaObjects);
        Assert.Equal(1, board.Current.History.UndoCount);

        board.Current.Undo();
        Assert.Equal(32, text.FontSize, 3);
        Assert.Equal(1, text.Columns);
        Assert.Equal("left", text.Align);
        Assert.Equal(30, formula.FontSize, 3);
        Assert.Equal(1, board.Current.History.RedoCount);
    }

    [Fact]
    public void ApplyAiObjectEdit_KeepFormulaDeletesOtherObjectsInOneUndo()
    {
        var board = new BoardService();
        var text = Text("保留检查", 10, 10);
        var formula = Formula("a^2+b^2", 10, 200);
        board.AddAiObjectsAtomic(new[] { text }, new[] { formula }, "AI 板书", "edit-group");
        board.Current.History.Clear();

        var edit = new BoardPlanEditPolicy.BoardObjectEdit();
        edit.KeepKinds.Add("formula");
        Assert.True(board.ApplyAiObjectEdit(new ISelectionElement[] { text, formula }, edit, "只保留公式"));

        Assert.Empty(board.CurrentTextObjects);
        Assert.Single(board.CurrentFormulaObjects);
        Assert.Equal(1, board.Current.History.UndoCount);

        board.Current.Undo();
        Assert.Single(board.CurrentTextObjects);
        Assert.Single(board.CurrentFormulaObjects);
        Assert.Equal("保留检查", board.CurrentTextObjects[0].Content);
    }

    [Fact]
    public void RemoveAiObjects_RestoresOriginalOrderWithOneUndo()
    {
        var board = new BoardService();
        var first = Text("一", 0, 0);
        var second = Text("二", 0, 200);
        var third = Text("三", 0, 400);
        var formula = Formula("a+b", 400, 0);
        board.AddAiObjectsAtomic(new[] { first, second, third }, new[] { formula }, "AI 板书");
        board.Current.History.Clear();

        Assert.True(board.RemoveAiObjects(new ISelectionElement[] { second, formula }, "删除 AI 对象"));
        Assert.Equal(new[] { first.Id, third.Id }, board.CurrentTextObjects.Select(text => text.Id));
        Assert.Empty(board.CurrentFormulaObjects);
        Assert.Equal(1, board.Current.History.UndoCount);

        board.Current.Undo();
        Assert.Equal(new[] { first.Id, second.Id, third.Id }, board.CurrentTextObjects.Select(text => text.Id));
        Assert.Equal(formula.Id, Assert.Single(board.CurrentFormulaObjects).Id);

        board.Current.Redo();
        Assert.Equal(new[] { first.Id, third.Id }, board.CurrentTextObjects.Select(text => text.Id));
        Assert.Empty(board.CurrentFormulaObjects);
    }

    [Fact]
    public async Task TextAndFormulaObjects_RoundTripThroughV4Packages()
    {
        string path = TempFile("roundtrip.icink");
        try
        {
            var board = new BoardService();
            board.SetBackground(BoardBackground.White);
            var text = Text("静夜思", 12.5f, 24.5f, "poem");
            text.Author = "李白";
            text.Dynasty = "唐";
            text.Align = "center";
            text.LineHeight = 1.8;
            text.Columns = 2;
            text.TransformMatrix = BoardElementMath.Write(Matrix3x2.CreateRotation(0.2f));
            var formula = Formula("\\sqrt{x^2+1}", 300.5f, 80.5f);
            formula.TransformMatrix = BoardElementMath.Write(Matrix3x2.CreateScale(1.25f, 0.8f));
            board.AddAiObjectsAtomic(new[] { text }, new[] { formula }, "AI 板书", "saved-group");

            await board.SaveBoardAsync(path);
            string fileText = File.ReadAllText(path);
            Assert.Contains("\"Version\": 4", fileText);
            Assert.Contains("\"TextObjects\"", fileText);
            Assert.Contains("\"FormulaObjects\"", fileText);

            var loaded = new BoardService();
            await loaded.LoadBoardAsync(path);

            BoardTextObject actualText = Assert.Single(loaded.CurrentTextObjects);
            Assert.Equal(text.Id, actualText.Id);
            Assert.Equal("静夜思", actualText.Content);
            Assert.Equal("标题", actualText.Title);
            Assert.Equal("李白", actualText.Author);
            Assert.Equal("唐", actualText.Dynasty);
            Assert.Equal("poem", actualText.Type);
            Assert.Equal("Microsoft YaHei UI", actualText.FontFamily);
            Assert.Equal(32, actualText.FontSize);
            Assert.Equal("#FF202024", actualText.Color);
            Assert.Equal(1.8, actualText.LineHeight);
            Assert.Equal("center", actualText.Align);
            Assert.Equal(2, actualText.Columns);
            Assert.Equal(12.5f, actualText.Left);
            Assert.Equal(24.5f, actualText.Top);
            Assert.Equal(320f, actualText.Width);
            Assert.Equal(140f, actualText.Height);
            Assert.Equal("saved-group", actualText.GroupId);
            Assert.Equal("ai", actualText.Source);
            Assert.Equal("整理当前页板书", actualText.Instruction);
            Assert.Equal(1730000000000, actualText.CreatedAt);
            Assert.True(BoardElementMath.Same(text.TransformMatrix, actualText.TransformMatrix));

            BoardFormulaObject actualFormula = Assert.Single(loaded.CurrentFormulaObjects);
            Assert.Equal(formula.Id, actualFormula.Id);
            Assert.Equal("\\sqrt{x^2+1}", actualFormula.Latex);
            Assert.Equal("Cambria Math", actualFormula.FontFamily);
            Assert.Equal(30, actualFormula.FontSize);
            Assert.Equal(300.5f, actualFormula.Left);
            Assert.Equal(80.5f, actualFormula.Top);
            Assert.Equal("saved-group", actualFormula.GroupId);
            Assert.True(BoardElementMath.Same(formula.TransformMatrix, actualFormula.TransformMatrix));

            Assert.Equal(2, loaded.GetSelectionElements().Count());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task OldV4FileWithoutNewFields_LoadsWithEmptyCollections()
    {
        string path = TempFile("legacy-v4.json");
        try
        {
            File.WriteAllText(path, """
            {
              "Format": "inkcanvas-board",
              "Version": 4,
              "Pages": [
                {
                  "Name": "旧页面",
                  "Background": "Black",
                  "GridSpacing": 40,
                  "Strokes": [],
                  "Images": [],
                  "Media": []
                }
              ]
            }
            """);

            var board = new BoardService();
            await board.LoadBoardAsync(path);

            Assert.Empty(board.CurrentTextObjects);
            Assert.Empty(board.CurrentFormulaObjects);
            Assert.Empty(board.GetSelectionElements());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public async Task SideBoardNativeObjects_RoundTripWithSideScope()
    {
        string path = TempFile("side.icink");
        try
        {
            var main = new BoardService();
            var side = new BoardService();
            main.SideBoardSource = side;
            var text = Text("子板文本", 10, 10);
            var formula = Formula("a^2+b^2=c^2", 10, 180);
            side.AddAiObjectsAtomic(new[] { text }, new[] { formula }, "AI 板书", "side-group");

            await main.SaveBoardAsync(path);

            var loaded = new BoardService();
            var loadedSide = new BoardService();
            loaded.SideBoardSource = loadedSide;
            await loaded.LoadBoardAsync(path);

            BoardTextObject actualText = Assert.Single(loadedSide.CurrentTextObjects);
            BoardFormulaObject actualFormula = Assert.Single(loadedSide.CurrentFormulaObjects);
            Assert.Equal(text.Id, actualText.Id);
            Assert.Equal(formula.Id, actualFormula.Id);
            Assert.Equal("side-group", actualText.GroupId);
            Assert.Equal("side-group", actualFormula.GroupId);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void DefaultAiInkAdaptsToPageBackgroundAndKeepsUserColors()
    {
        var board = new BoardService();
        var blackText = Text("黑板总结", 0, 0);
        var blackFormula = Formula("a+b", 0, 200);
        board.AddAiObjectsAtomic(new[] { blackText }, new[] { blackFormula }, "AI 板书");

        Assert.Equal("#FFF5F5F8", blackText.Color);
        Assert.Equal("#FFF5F5F8", blackFormula.Color);

        var redText = Text("用户红色", 0, 400);
        redText.Color = "#FFAA0000";
        board.AddAiObjectsAtomic(new[] { redText }, null, "AI 板书");
        Assert.Equal("#FFAA0000", redText.Color);

        board.SetBackground(BoardBackground.White);
        var lightText = Text("浅色字", 0, 0);
        lightText.Color = "#FFF5F5F8";
        board.AddAiObjectsAtomic(new[] { lightText }, null, "AI 板书");
        Assert.Equal("#FF202024", lightText.Color);

        var darkText = Text("默认深色", 0, 200);
        board.AddAiObjectsAtomic(new[] { darkText }, null, "AI 板书");
        Assert.Equal("#FF202024", darkText.Color);
    }
}