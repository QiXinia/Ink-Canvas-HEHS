using InkCanvas.Core.Ink;
using System.Text.Json;

namespace InkCanvas.Core.Classroom;

/// <summary>
/// 单笔序列化为 JSON。字段布局与 <c>InkCanvas.Features.Persistence.InkFileStore</c> 的
/// StrokeModel 完全一致（Color/Width/Highlighter/HighlighterAlpha/Taper/Shape/Points），
/// 因此录制里的笔画与既有 .icink 存档可互换，不另造格式。
/// </summary>
public static class StrokeJsonCodec
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private sealed class StrokeModel
    {
        public uint Color { get; set; }
        public float Width { get; set; }
        public bool Highlighter { get; set; }
        public byte HighlighterAlpha { get; set; } = 96;
        public int Taper { get; set; }
        public bool Shape { get; set; }
        /// <summary>Rows of [x, y, pressure, width, timeMs].</summary>
        public List<float[]> Points { get; set; } = new();
    }

    public static string Serialize(InkStroke stroke)
    {
        if (stroke is null) return null;
        var model = new StrokeModel
        {
            Color = stroke.Attributes.Color.PackedArgb,
            Width = stroke.Attributes.BaseWidth,
            Highlighter = stroke.Attributes.IsHighlighter,
            HighlighterAlpha = stroke.Attributes.HighlighterAlpha,
            Taper = (int)stroke.TaperKind,
            Shape = stroke.IsRecognizedShape,
        };
        foreach (var p in stroke.Points)
            model.Points.Add(new[] { p.X, p.Y, p.Pressure, p.Width, p.TimeMs });
        return JsonSerializer.Serialize(model, Options);
    }

    public static InkStroke Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        var model = JsonSerializer.Deserialize<StrokeModel>(json, Options);
        if (model is null) return null;

        var attributes = new StrokeAttributes
        {
            Color = InkColor.FromPackedArgb(model.Color),
            BaseWidth = model.Width,
            IsHighlighter = model.Highlighter,
            HighlighterAlpha = model.HighlighterAlpha,
            TaperKind = Enum.IsDefined(typeof(TaperKind), model.Taper) ? (TaperKind)model.Taper : TaperKind.None,
        };
        var stroke = new InkStroke(attributes) { IsRecognizedShape = model.Shape };
        stroke.RestorePoints((model.Points ?? new List<float[]>())
            .Where(row => row.Length >= 4)
            .Select(row => new InkPoint(row[0], row[1], row[2], row[3], row.Length > 4 ? row[4] : 0f)));
        stroke.Complete();
        return stroke;
    }
}