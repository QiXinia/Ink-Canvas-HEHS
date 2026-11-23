using System.Numerics;
using InkCanvas.Core.Classroom;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using InkCanvas.Ink;

namespace InkCanvas.Features.Classroom;

/// <summary>Read-only replay adapter for a structured native AI object snapshot.</summary>
public abstract class TimelineNativeElementBase : ISelectionElement
{
    protected TimelineNativeElementBase(TimelineAiObject item)
    {
        Left = (float)item.Left;
        Top = (float)item.Top;
        Width = (float)Math.Max(1, item.Width);
        Height = (float)Math.Max(1, item.Height);
        LayerOrder = item.LayerOrder;
        Id = Guid.TryParse(item.Id, out Guid id) ? id : Guid.Empty;
    }

    public Guid Id { get; }

    public long? LayerOrder { get; set; }

    public float Left { get; }

    public float Top { get; }

    public float Width { get; }

    public float Height { get; }

    public Rect Bounds => new(Left, Top, Left + Width, Top + Height);

    public SelectionFrame Frame => new(Bounds, Matrix3x2.Identity);

    public bool IsPresent => true;

    public object Capture() => this;

    public void Restore(object snapshot) { }

    public void Transform(Matrix3x2 delta) { }

    public void TransformFrom(object snapshot, Matrix3x2 delta) { }

    public ISelectionElement Duplicate() => this;

    public void SetPresent(bool present) { }
}

/// <summary>Vector text replay adapter: drives <c>AiTextSceneRenderer</c> without any PNG.</summary>
public sealed class TimelineNativeTextElement : TimelineNativeElementBase, IBoardTextElement
{
    public TimelineNativeTextElement(TimelineAiObject item) : base(item)
    {
        Type = string.IsNullOrWhiteSpace(item.Kind) ? "text" : item.Kind;
        Content = item.Content ?? "";
        Title = item.Title ?? "";
        Author = item.Author ?? "";
        Dynasty = item.Dynasty ?? "";
        FontFamily = string.IsNullOrWhiteSpace(item.FontFamily) ? "Microsoft YaHei UI" : item.FontFamily;
        FontSize = item.FontSize > 0 ? item.FontSize : 32;
        Color = string.IsNullOrWhiteSpace(item.Color) ? "#FF202024" : item.Color;
        LineHeight = item.LineHeight > 0 ? item.LineHeight : 1.4;
        Align = string.IsNullOrWhiteSpace(item.Align) ? "left" : item.Align;
        Columns = item.Columns <= 0 ? 1 : Math.Clamp(item.Columns, 1, 3);
    }

    public string Type { get; }
    public string Content { get; }
    public string Title { get; }
    public string Author { get; }
    public string Dynasty { get; }
    public string FontFamily { get; }
    public double FontSize { get; }
    public string Color { get; }
    public double LineHeight { get; }
    public string Align { get; }
    public int Columns { get; }
}

/// <summary>Vector formula replay adapter: drives <c>FormulaSceneRenderer</c> from stored LaTeX.</summary>
public sealed class TimelineNativeFormulaElement : TimelineNativeElementBase, IBoardFormulaElement
{
    public TimelineNativeFormulaElement(TimelineAiObject item) : base(item)
    {
        Latex = item.Latex ?? "";
        FontFamily = string.IsNullOrWhiteSpace(item.FontFamily) ? "Cambria Math" : item.FontFamily;
        FontSize = item.FontSize > 0 ? item.FontSize : 30;
        Color = string.IsNullOrWhiteSpace(item.Color) ? "#FF202024" : item.Color;
    }

    public string Latex { get; }
    public string FontFamily { get; }
    public double FontSize { get; }
    public string Color { get; }
}