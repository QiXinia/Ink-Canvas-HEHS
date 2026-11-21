namespace InkCanvas.Core.Ink;

/// <summary>Per-stroke water-ink brush parameters. Fractions are in [0,1], angle is in degrees.</summary>
public readonly record struct InkWashBrushOptions(float Dryness, float Angle, float Flatness)
{
    public static InkWashBrushOptions Default => new(.42f, 44, .44f);

    public InkWashBrushOptions Normalize() => new(
        float.IsFinite(Dryness) ? Math.Clamp(Dryness, 0, 1) : Default.Dryness,
        float.IsFinite(Angle) ? Math.Clamp(Angle, -180, 180) : Default.Angle,
        float.IsFinite(Flatness) ? Math.Clamp(Flatness, 0, .9f) : Default.Flatness);
}
