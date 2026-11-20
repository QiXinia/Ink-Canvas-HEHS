namespace InkCanvas.Core.Ink;

public enum TaperKind
{
    /// <summary>Uniform width.</summary>
    None = 0,

    /// <summary>Nib taper: thin at the moving tip, settling to full width behind it.</summary>
    Tail = 1,

    /// <summary>Width driven by drawing speed (fast = thinner).</summary>
    Velocity = 2,

    SmoothPen = 3,
    SoftPen = 4,
    Natural = 5,
    Calligraphy = 6,
    InkWash = 7,
}

/// <summary>
/// Computes per-point stroke width <b>incrementally and causally</b>.
/// <para>
/// This is the fix for the stroke-taper flicker. The WPF original computed the taper in
/// <c>StrokeCollected</c> — after pen-up — and replaced the whole point collection, so the
/// tail (or, in velocity mode, the entire stroke) visibly snapped to a new width on lift.
/// </para>
/// <para>
/// Contract every implementation must honour:
/// <list type="number">
/// <item>Only indices <c>&lt;= tipIndex</c> may be read or written — never a future point.</item>
/// <item>A point's width may only ever <b>increase</b> over successive calls. It settles
/// (freezes) once the tip has moved past the profile's window.</item>
/// <item>Completing a stroke changes nothing, so the committed geometry equals the last
/// live frame exactly.</item>
/// </list>
/// </para>
/// </summary>
public abstract class TaperProfile
{
    public abstract TaperKind Kind { get; }

    /// <param name="points">All points sampled so far; widths are updated in place.</param>
    /// <param name="tipIndex">Index of the point that just arrived.</param>
    /// <param name="baseWidth">Full stroke width.</param>
    public abstract void Apply(Span<InkPoint> points, int tipIndex, float baseWidth);
    public virtual int StablePointCount(int count) => count;

    /// <summary>Monotonic width assignment: never shrink an already-drawn point.</summary>
    protected static void Settle(ref float slot, float candidate)
    {
        if (candidate > slot) slot = candidate;
    }

    protected static float PressureWidth(in InkPoint point, float width)
        => point.HasPressure ? width * (.25f + 1.5f * Math.Clamp(point.Pressure, 0, 1)) : width;

    public static TaperProfile Create(TaperKind kind) => kind switch
    {
        TaperKind.None => new NoneTaper(),
        TaperKind.Velocity => new VelocityTaper(),
        TaperKind.SmoothPen or TaperKind.SoftPen or TaperKind.Natural or TaperKind.Calligraphy or TaperKind.InkWash => new SimulatedPressureTaper(kind),
        _ => new TailTaper(),
    };
}

public sealed class NoneTaper : TaperProfile
{
    public override TaperKind Kind => TaperKind.None;

    public override void Apply(Span<InkPoint> points, int tipIndex, float baseWidth)
        => Settle(ref points[tipIndex].Width, PressureWidth(points[tipIndex], baseWidth));
}

/// <summary>
/// Nib taper over a trailing window of <see cref="TailPoints"/> samples, plus a short
/// entry taper. Because the window slides with the tip, the only pixels whose width
/// changes frame-to-frame are the ones right at the tip — which reads as a pen nib,
/// not as a flicker.
/// </summary>
public sealed class TailTaper : TaperProfile
{
    public override TaperKind Kind => TaperKind.Tail;

    /// <summary>Trailing window length in samples. The original used 10.</summary>
    public int TailPoints { get; init; } = 10;

    /// <summary>Entry taper length in samples.</summary>
    public int HeadPoints { get; init; } = 4;

    /// <summary>Width ratio at the very tip. Original pressure 0.1 against 0.5 baseline.</summary>
    public float TipRatio { get; init; } = 0.2f;
    public override int StablePointCount(int count) => Math.Max(0, count - Math.Max(2, TailPoints));

    public override void Apply(Span<InkPoint> points, int tipIndex, float baseWidth)
    {
        int n = Math.Max(2, TailPoints);
        int start = Math.Max(0, tipIndex - n + 1);
        for (int j = start; j <= tipIndex; j++)
        {
            float distance = tipIndex - j;                       // 0 at the tip
            float t = distance / (float)(n - 1);                  // 0 at tip -> 1 at window edge
            float ratio = TipRatio + (1f - TipRatio) * t;         // thin at tip, full behind it
            Settle(ref points[j].Width, PressureWidth(points[j], baseWidth) * ratio);
        }

        if (tipIndex < HeadPoints)
        {
            float t = (tipIndex + 1) / (float)(HeadPoints + 1);
            float ratio = TipRatio + (1f - TipRatio) * t;
            Settle(ref points[tipIndex].Width, PressureWidth(points[tipIndex], baseWidth) * ratio);
        }
    }
}

/// <summary>
/// Speed-driven width, reproducing the original <c>InkStyle=1</c> curve but causally.
/// The original measured spread around point <c>i</c> using <c>i-1</c> and <c>i+1</c>;
/// referencing <c>i+1</c> is what forced it to recompute the whole stroke after pen-up.
/// Here we use the two <b>already available</b> segments ending at <c>i</c>, so each
/// width is decided once, on arrival, and never revisited.
/// </summary>
public sealed class VelocityTaper : TaperProfile
{
    public override TaperKind Kind => TaperKind.Velocity;

    public override void Apply(Span<InkPoint> points, int tipIndex, float baseWidth)
    {
        float pressure = 0.5f;
        if (tipIndex >= 2)
        {
            float d1 = Dist(points[tipIndex - 2], points[tipIndex - 1]);
            float d2 = Dist(points[tipIndex - 1], points[tipIndex]);
            float speed = (d1 + d2) / 20f;   // same scale as the original GetPointSpeed

            if (speed >= 0.25f)
                pressure = (float)(0.5 - 0.3 * (Math.Min(speed, 1.5f) - 0.3) / 1.2);
            else if (speed < 0.05f)
                pressure = (float)(0.5 + 0.4 * (0.05 - speed) / 0.05);
        }

        // Original pressure baseline is 0.5, so 0.5 maps to the full base width.
        float ratio = Math.Clamp(pressure / 0.5f, 0.15f, 1.8f);
        Settle(ref points[tipIndex].Width, PressureWidth(points[tipIndex], baseWidth) * ratio);
    }

    private static float Dist(in InkPoint a, in InkPoint b)
    {
        float dx = a.X - b.X, dy = a.Y - b.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
