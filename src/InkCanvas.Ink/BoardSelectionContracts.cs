namespace InkCanvas.Ink;

/// <summary>Optional selection grouping shared by AI objects written in one gesture.</summary>
public interface IGroupedSelectionElement : ISelectionElement
{
    /// <summary>Empty means the element is independent; non-empty elements share selection gestures.</summary>
    string SelectionGroupId { get; set; }
}

/// <summary>Render contract for a native, editable board text object (never a baked bitmap).</summary>
public interface IBoardTextElement : ISelectionElement
{
    string Type { get; }
    string Content { get; }
    string Title { get; }
    string Author { get; }
    string Dynasty { get; }
    string FontFamily { get; }
    double FontSize { get; }
    string Color { get; }
    double LineHeight { get; }
    string Align { get; }
    int Columns { get; }
}

/// <summary>Render contract for a native formula object; the LaTeX source is always retained.</summary>
public interface IBoardFormulaElement : ISelectionElement
{
    string Latex { get; }
    string FontFamily { get; }
    double FontSize { get; }
    string Color { get; }
}