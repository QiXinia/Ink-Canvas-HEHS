using System.Numerics;

namespace InkCanvas.Core.Ink;

public enum PenNib { Round, Ellipse45 }

public sealed class StrokeAttributes
{
    public InkColor Color { get; set; } = InkColor.Black;

    /// <summary>Full stroke width in device-independent pixels.</summary>
    public float BaseWidth { get; set; } = 3f;

    public bool IsHighlighter { get; set; }

    /// <summary>Original WPF Canvas.InkAlpha (20–240, step 20); applies to highlighter only.</summary>
    public byte HighlighterAlpha { get; set; } = 96;

    public TaperKind TaperKind { get; set; } = TaperKind.Tail;
    public PenNib Nib { get; set; }
    public float NibRotation { get; set; }
    public InkWashBrushOptions InkWash { get; set; } = InkWashBrushOptions.Default;
    public int InkWashSeed { get; set; }
    /// <summary>Construction-only override for the preset generator's hidden/dashed edges.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool GeneratorSolidLines { get; set; }
    /// <summary>Width specifies the major diameter. The oval has a 2:1 aspect ratio and rises to the right.</summary>
    public Matrix3x2 NibTransform => Nib == PenNib.Ellipse45
        ? Matrix3x2.CreateScale(1, .5f) * Matrix3x2.CreateRotation(-MathF.PI / 4 + NibRotation)
        : Matrix3x2.Identity;

    /// <summary>Highlighter ink is laid down translucent and never tapered.</summary>
    public TaperKind EffectiveTaper => IsHighlighter ? TaperKind.None : TaperKind;

    public InkColor EffectiveColor => IsHighlighter ? Color.WithAlpha(HighlighterAlpha) : Color;

    public StrokeAttributes Clone() => new()
    {
        Color = Color,
        BaseWidth = BaseWidth,
        IsHighlighter = IsHighlighter,
        HighlighterAlpha = HighlighterAlpha,
        TaperKind = TaperKind,
        Nib = Nib,
        NibRotation = NibRotation,
        InkWash = InkWash,
        InkWashSeed = InkWashSeed,
        GeneratorSolidLines = GeneratorSolidLines,
    };
}
