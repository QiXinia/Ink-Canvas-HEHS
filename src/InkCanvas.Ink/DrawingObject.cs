using InkCanvas.Core.Ink;

namespace InkCanvas.Ink;

public enum LayerAction { BringToFront, MoveForward, MoveBackward, SendToBack }

/// <summary>A drawing entry in the shared back-to-front order of ink, shapes and embedded objects.</summary>
public readonly record struct DrawingObject(InkStroke Stroke, ISelectionElement Element)
{
    public Guid Id => Stroke?.Id ?? Element.Id;
    public int InkLayerIndex => Stroke?.InkLayerIndex ?? 0;
    public long? LayerOrder
    {
        get => Stroke is not null ? Stroke.LayerOrder : Element.LayerOrder;
        set { if (Stroke is not null) Stroke.LayerOrder = value; else Element.LayerOrder = value; }
    }
    public long SortOrder => LayerOrder ?? (Stroke is not null ? long.MaxValue
        : Element is IImageSelectionElement { ImageData: not null } ? long.MinValue : long.MinValue + 1);
}
