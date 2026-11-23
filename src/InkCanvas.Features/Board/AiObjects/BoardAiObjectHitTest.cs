using System.Numerics;
using InkCanvas.Ink;

namespace InkCanvas.Features.Board.AiObjects;

/// <summary>
/// 世界坐标命中原生 AI 文本/公式对象；按 LayerOrder 取最上层。
/// 命中盒使用与 <c>CommittedInkLayer</c> 绘制一致的 <c>Bounds</c>（已含 TransformMatrix），
/// 并保留少量容差；调用方用 <see cref="HitTestView"/> 把视图坐标反变换到世界坐标。
/// </summary>
public static class BoardAiObjectHitTest
{
    /// <summary>世界坐标容差（DIP）：允许点在对象边缘外少量距离仍命中。</summary>
    public const float HitTolerance = 2f;

    public static ISelectionElement HitTest(
        IReadOnlyList<BoardTextObject> texts,
        IReadOnlyList<BoardFormulaObject> formulas,
        float worldX,
        float worldY)
        => HitTest(texts, formulas, worldX, worldY, out _, out _);

    /// <summary>世界坐标命中；同时返回对象总数与离命中点最近的对象边界距离（未命中诊断用）。</summary>
    public static ISelectionElement HitTest(
        IReadOnlyList<BoardTextObject> texts,
        IReadOnlyList<BoardFormulaObject> formulas,
        float worldX,
        float worldY,
        out int objectCount,
        out float nearestDistance)
    {
        objectCount = (texts?.Count ?? 0) + (formulas?.Count ?? 0);
        nearestDistance = float.PositiveInfinity;

        ISelectionElement best = null;
        long bestLayer = long.MinValue;

        foreach (BoardTextObject text in texts ?? Array.Empty<BoardTextObject>())
        {
            if (text is null) continue;
            Track(text.Bounds, text.LayerOrder, text, worldX, worldY, ref best, ref bestLayer, ref nearestDistance);
        }

        foreach (BoardFormulaObject formula in formulas ?? Array.Empty<BoardFormulaObject>())
        {
            if (formula is null) continue;
            Track(formula.Bounds, formula.LayerOrder, formula, worldX, worldY, ref best, ref bestLayer, ref nearestDistance);
        }

        return best;
    }

    /// <summary>把视图坐标按 ViewTransform 逆变换后命中；返回转换后的世界坐标。</summary>
    public static ISelectionElement HitTestView(
        IReadOnlyList<BoardTextObject> texts,
        IReadOnlyList<BoardFormulaObject> formulas,
        float viewX,
        float viewY,
        Matrix3x2 viewTransform,
        out Vector2 world,
        out int objectCount,
        out float nearestDistance)
    {
        world = new Vector2(viewX, viewY);
        if (!Matrix3x2.Invert(viewTransform, out Matrix3x2 inverse))
        {
            objectCount = (texts?.Count ?? 0) + (formulas?.Count ?? 0);
            nearestDistance = float.PositiveInfinity;
            return null;
        }

        world = Vector2.Transform(world, inverse);
        return HitTest(texts, formulas, world.X, world.Y, out objectCount, out nearestDistance);
    }

    private static void Track(Core.Ink.Rect bounds, long? layerOrder, ISelectionElement element,
        float x, float y, ref ISelectionElement best, ref long bestLayer, ref float nearestDistance)
    {
        float distance = DistanceToBounds(bounds, x, y);
        if (distance < nearestDistance) nearestDistance = distance;

        if (!Contains(bounds, x, y)) return;
        long layer = layerOrder ?? long.MinValue;
        if (best is null || layer >= bestLayer)
        {
            best = element;
            bestLayer = layer;
        }
    }

    private static bool Contains(Core.Ink.Rect bounds, float x, float y)
    {
        if (bounds.IsEmpty) return false;
        Core.Ink.Rect tolerant = bounds.Inflate(HitTolerance);
        return x >= tolerant.Left && x <= tolerant.Right && y >= tolerant.Top && y <= tolerant.Bottom;
    }

    private static float DistanceToBounds(Core.Ink.Rect bounds, float x, float y)
    {
        if (bounds.IsEmpty) return float.PositiveInfinity;
        float dx = x < bounds.Left ? bounds.Left - x : x > bounds.Right ? x - bounds.Right : 0;
        float dy = y < bounds.Top ? bounds.Top - y : y > bounds.Bottom ? y - bounds.Bottom : 0;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}