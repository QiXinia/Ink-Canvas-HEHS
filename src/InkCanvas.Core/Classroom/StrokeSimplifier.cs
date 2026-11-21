using InkCanvas.Core.Ink;

namespace InkCanvas.Core.Classroom;

/// <summary>
/// 笔画点抽稀（Ramer–Douglas–Peucker）。
/// <para>
/// 录制提交时对采样点做一次简化，减少会话体积；容差是中心线最大偏离（DIP），
/// 默认 0.8 DIP，回放视觉上与原始笔画几乎无差别。首尾点始终保留，结果至少 2 点
/// （原始不足 2 点时原样复制）。属性、压力、点宽与时间戳都随保留点一起保留，
/// 且始终返回新对象，不修改传入的笔画。
/// </para>
/// </summary>
public static class StrokeSimplifier
{
    public const double DefaultTolerance = 0.8;

    /// <summary>简化点列，返回新的点集合；不修改 <paramref name="points"/>。</summary>
    public static IReadOnlyList<InkPoint> Simplify(IReadOnlyList<InkPoint> points, double tolerance = DefaultTolerance)
    {
        if (points is null || points.Count == 0) return Array.Empty<InkPoint>();
        if (points.Count <= 2)
        {
            var copy = new InkPoint[points.Count];
            for (int i = 0; i < points.Count; i++) copy[i] = points[i];
            return copy;
        }

        if (!(tolerance > 0) || double.IsNaN(tolerance) || double.IsInfinity(tolerance))
        {
            var copy = new InkPoint[points.Count];
            for (int i = 0; i < points.Count; i++) copy[i] = points[i];
            return copy;
        }

        float toleranceF = (float)tolerance;
        int count = points.Count;
        var keep = new bool[count];
        keep[0] = true;
        keep[count - 1] = true;

        var pending = new Stack<(int First, int Last)>();
        pending.Push((0, count - 1));

        while (pending.Count > 0)
        {
            var (first, last) = pending.Pop();
            if (last - first < 2) continue;

            float maxDistance = 0f;
            int maxIndex = -1;
            var a = points[first].Position;
            var b = points[last].Position;

            for (int i = first + 1; i < last; i++)
            {
                float distance = InkStroke.DistanceToSegment(points[i].Position, a, b);
                if (distance > maxDistance)
                {
                    maxDistance = distance;
                    maxIndex = i;
                }
            }

            if (maxIndex < 0 || maxDistance <= toleranceF) continue;

            keep[maxIndex] = true;
            pending.Push((first, maxIndex));
            pending.Push((maxIndex, last));
        }

        int kept = 0;
        for (int i = 0; i < count; i++) if (keep[i]) kept++;

        var result = new InkPoint[kept];
        int cursor = 0;
        for (int i = 0; i < count; i++)
        {
            if (keep[i]) result[cursor++] = points[i];
        }
        return result;
    }

    /// <summary>
    /// 简化整笔：保留 Id、属性、压力 / 点宽 / 时间戳、识别形状标记与完成状态，
    /// 返回新的 <see cref="InkStroke"/>。
    /// </summary>
    public static InkStroke Simplify(InkStroke stroke, double tolerance = DefaultTolerance)
    {
        if (stroke is null) throw new ArgumentNullException(nameof(stroke));

        var copy = new InkStroke(stroke.Attributes.Clone())
        {
            Id = stroke.Id,
            IsRecognizedShape = stroke.IsRecognizedShape,
        };
        copy.RestorePoints(Simplify(stroke.Points, tolerance));
        if (stroke.IsCompleted) copy.Complete();
        return copy;
    }
}