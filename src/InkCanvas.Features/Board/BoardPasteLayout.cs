using System.Numerics;
using InkCanvas.Ink;

namespace InkCanvas.Features.Board;

/// <summary>贴回主展板时的屏幕空间矩形与文档空间变换。</summary>
public readonly record struct BoardPasteRect(float Left, float Top, float Width, float Height, float[] Transform);

/// <summary>
/// 板中板内容贴回主展板的比例与位置计算。坐标语义与 <c>ImageImport.PlaceInView</c> 一致：
/// Left/Top/Width/Height 为屏幕（视口）DIP，Transform 为世界→视图矩阵的逆矩阵。
/// 纯数学实现，可在无 UI 线程的测试中验证。
/// </summary>
public static class BoardPasteLayout
{
    public const float DefaultMaxVisibleWidthFraction = 0.6f;

    public static BoardPasteRect FitCentered(float contentWidth, float contentHeight,
        float viewportWidth, float viewportHeight, Matrix3x2 view,
        float maxVisibleWidthFraction = DefaultMaxVisibleWidthFraction)
    {
        if (!(contentWidth > 0) || !(contentHeight > 0) || !(viewportWidth > 0) || !(viewportHeight > 0))
            return default;

        float width = contentWidth, height = contentHeight;
        float fraction = float.IsFinite(maxVisibleWidthFraction)
            ? Math.Clamp(maxVisibleWidthFraction, 0.01f, 1f)
            : DefaultMaxVisibleWidthFraction;
        float scale = Math.Min(1, Math.Min(viewportWidth * fraction / width, viewportHeight * 0.8f / height));
        width *= scale;
        height *= scale;
        if (!(width > 0) || !(height > 0)) return default;

        float left = (viewportWidth - width) / 2f;
        float top = (viewportHeight - height) / 2f;
        float[] transform = Matrix3x2.Invert(view, out var inverse) ? BoardElementMath.Write(inverse) : null;
        return new BoardPasteRect(left, top, width, height, transform);
    }

    /// <summary>生成可交给 <see cref="BoardService.AddImage"/> 的图片资产；参数无效时返回 null。</summary>
    public static BoardImageAsset CreateImageAsset(string name, byte[] png,
        float contentWidth, float contentHeight, float viewportWidth, float viewportHeight,
        Matrix3x2 view, float maxVisibleWidthFraction = DefaultMaxVisibleWidthFraction)
    {
        if (png is not { Length: > 0 }) return null;
        var rect = FitCentered(contentWidth, contentHeight, viewportWidth, viewportHeight, view, maxVisibleWidthFraction);
        if (!(rect.Width > 0) || !(rect.Height > 0)) return null;
        return new BoardImageAsset
        {
            Name = string.IsNullOrWhiteSpace(name) ? "板中板.png" : name,
            Data = png,
            ContentType = "image/png",
            Left = rect.Left,
            Top = rect.Top,
            Width = rect.Width,
            Height = rect.Height,
            Transform = rect.Transform,
        };
    }
}