using InkCanvas.Platform.Windows;

namespace InkCanvas.Ink;

public enum InkPointerPhase { Hover, Down, Move, Up, Canceled, Exit }
public readonly record struct InkPointerInput(InkController Controller, OverlayPointer Pointer, InkPointerPhase Phase);

public sealed partial class InkController
{
    /// <summary>Optional input extensions run on the owning UI thread before ink is routed and after contact ends.</summary>
    public static event Action<InkPointerInput> PointerInput;
    public static event Action<InkController> InputContextChanging;
    public void NotifyInputContextChanging() => InputContextChanging?.Invoke(this);

    public void OnPointerDown(in OverlayPointer pointer)
    {
        PointerInput?.Invoke(new(this, pointer, InkPointerPhase.Down));
        OnPointerDownCore(pointer);
    }

    public void OnPointerMove(in OverlayPointer pointer)
    {
        PointerInput?.Invoke(new(this, pointer, InkPointerPhase.Move));
        OnPointerMoveCore(pointer);
    }

    public void OnPointerUp(in OverlayPointer pointer, bool canceled = false)
    {
        try { OnPointerUpCore(pointer, canceled); }
        finally { PointerInput?.Invoke(new(this, pointer, canceled ? InkPointerPhase.Canceled : InkPointerPhase.Up)); }
    }

    public void OnPointerExited(uint pointerId)
    {
        PointerInput?.Invoke(new(this, new OverlayPointer { Id = pointerId }, InkPointerPhase.Exit));
        HideEraserPreview(pointerId);
    }
}
