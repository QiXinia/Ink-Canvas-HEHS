using System.Numerics;
using InkCanvas.Core.Selection;
using InkCanvas.Platform.Windows;

namespace InkCanvas.Ink;

public sealed partial class InkController
{
    private (uint First, uint Second)? _selectionTouchPair;
    private Matrix3x2 _selectionTouchStartPreview = Matrix3x2.Identity;

    private void BeginSelectionTouchPair(in OverlayPointer first, in OverlayPointer second)
    {
        // A repeated ID lets the remaining finger continue translating after a pinch.
        _selectionTouchPair = (first.Id, second.Id);
        _viewAtDragStart = ViewTransform;
        _selectionTouchStartPreview = _selectionPreview;
        _selectionManipulator.AllowZoom = MultiTouchEnabled || _manipulator.AllowZoom;
        _selectionManipulator.AllowTranslate = MultiTouchEnabled || _manipulator.AllowTranslate;
        _selectionManipulator.AllowRotation = GesturesTargetSelection && (MultiTouchEnabled || !CloneMode);
        _selectionManipulator.Begin(new Vector2(first.X, first.Y), new Vector2(second.X, second.Y));
    }

    private bool TryGetSelectionTouchPair(out OverlayPointer first, out OverlayPointer second, long surface)
    {
        first = default;
        second = default;
        if (!TouchGesturesEnabled) return false;
        bool found = false;
        var frame = SelectionFrame;
        foreach (var touch in _touches.Values)
        {
            if (touch.InputSurface != surface) continue;
            if (MultiTouchEnabled && frame.HitTest(new Vector2(touch.X, touch.Y), ViewTransform) == SelectionHandle.None) continue;
            if (!found) { first = touch; found = true; }
            else { second = touch; return true; }
        }
        return false;
    }
}
