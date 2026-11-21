namespace InkCanvas.Core.Ink;

/// <summary>Fixed vector layers shared by screen, preview and PDF rendering.</summary>
public static class InkBrush
{
    public readonly record struct Layer(float WidthScale, byte Opacity)
    {
        public InkColor Color(InkColor color) => color.WithAlpha((byte)((color.A * Opacity + 127) / 255));
    }

    private static readonly Layer[] Solid = { new(1, 255) };
    private static readonly Layer[] Wash = { new(1, 184), new(1, 77), new(1, 199), new(1, 240) };

    public static bool UsesInkWash(InkStroke stroke) => !stroke.Attributes.IsHighlighter && !stroke.IsRecognizedShape
        && stroke.TaperKind == TaperKind.InkWash && stroke.Count > 1;

    public static ReadOnlySpan<Layer> Layers(InkStroke stroke) =>
        UsesInkWash(stroke) ? Wash : Solid;
}
