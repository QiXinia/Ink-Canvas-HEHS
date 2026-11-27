namespace InkCanvas.Core.Input;

/// <summary>Physical-pixel drag state that expires when input dispatch is interrupted.</summary>
public sealed class WindowDragSession
{
    private int _windowX, _windowY, _pointerX, _pointerY;
    private long _lastSample;
    public bool IsPressed { get; private set; }
    public bool IsDragging { get; private set; }

    public void Begin(int windowX, int windowY, int pointerX, int pointerY, long timestamp)
    {
        _windowX = windowX; _windowY = windowY;
        _pointerX = pointerX; _pointerY = pointerY;
        _lastSample = timestamp;
        IsPressed = true;
        IsDragging = false;
    }

    public bool TryMove(int pointerX, int pointerY, long timestamp, double threshold, out (int X, int Y) position)
    {
        position = default;
        if (!IsPressed) return false;
        if (timestamp < _lastSample || timestamp - _lastSample > 1000)
        {
            End();
            return false;
        }
        _lastSample = timestamp;
        int dx = pointerX - _pointerX, dy = pointerY - _pointerY;
        if (!IsDragging && (double)dx * dx + (double)dy * dy < threshold * threshold) return false;
        IsDragging = true;
        position = (_windowX + dx, _windowY + dy);
        return true;
    }

    public bool End()
    {
        bool moved = IsDragging;
        IsPressed = IsDragging = false;
        return moved;
    }
}
