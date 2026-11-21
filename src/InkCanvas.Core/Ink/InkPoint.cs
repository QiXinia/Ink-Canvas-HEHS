namespace InkCanvas.Core.Ink;

/// <summary>
/// One sampled pen/touch sample.
/// <para>
/// <see cref="Width"/> is stored, not derived at draw time. That is the structural
/// guarantee behind the stroke-taper flicker fix: rendering is a pure function of
/// stored data, so the committed stroke renders exactly like the last live frame.
/// </para>
/// </summary>
public struct InkPoint
{
    public float X;
    public float Y;

    /// <summary>Raw device pressure in [0,1]. HasPressure distinguishes hardware data from a fallback 0.5.</summary>
    public float Pressure;

    /// <summary>Final rendered width in device-independent pixels. Frozen once settled.</summary>
    public float Width;

    /// <summary>Milliseconds since stroke start.</summary>
    public float TimeMs;
    public bool HasPressure;
    public float TiltX;
    public float TiltY;

    public InkPoint(float x, float y, float pressure, float width, float timeMs,
        bool hasPressure = false, float tiltX = 0, float tiltY = 0)
    {
        X = x; Y = y; Pressure = pressure; Width = width; TimeMs = timeMs;
        HasPressure = hasPressure; TiltX = tiltX; TiltY = tiltY;
    }

    public readonly System.Numerics.Vector2 Position => new(X, Y);
}
