namespace InkCanvas.Core.AiAssistant;

/// <summary>图形释义标注点（0..1 归一化坐标）。</summary>
public readonly record struct ShapeAnnotationPoint(double X, double Y);

/// <summary>
/// 图形释义的纯几何逻辑：把服务端批注点归一化为 0..1，并按资产几何投影为局部 DIP。
/// 渲染层只做绘制，移动/缩放资产时由资产 Left/Top/Width/Height 重新投影即可。
/// </summary>
public static class ShapeAnnotationGeometry
{
    /// <summary>
    /// 展平点列表 [x1,y1,x2,y2,...] 归一化为 0..1。
    /// 坐标已落在 0..1 时原样保留；否则视为原始像素并按资产宽高归一。
    /// </summary>
    public static List<ShapeAnnotationPoint> Normalize(IReadOnlyList<double> points, double width, double height)
    {
        var result = new List<ShapeAnnotationPoint>();
        if (points is null || points.Count < 2) return result;

        bool unit = IsUnitRange(points);
        double divisorX = width > 0 ? width : 1;
        double divisorY = height > 0 ? height : 1;

        for (int i = 0; i + 1 < points.Count; i += 2)
        {
            double x = points[i];
            double y = points[i + 1];
            if (!unit)
            {
                x /= divisorX;
                y /= divisorY;
            }

            result.Add(new ShapeAnnotationPoint(Clamp01(x), Clamp01(y)));
        }

        return result;
    }

    /// <summary>把归一化点投影到资产所在坐标系的 DIP 位置。</summary>
    public static List<ShapeAnnotationPoint> Project(
        IReadOnlyList<ShapeAnnotationPoint> normalized, double left, double top, double width, double height)
    {
        var result = new List<ShapeAnnotationPoint>();
        if (normalized is null) return result;

        double w = Math.Max(0, width);
        double h = Math.Max(0, height);
        foreach (ShapeAnnotationPoint point in normalized)
            result.Add(new ShapeAnnotationPoint(left + point.X * w, top + point.Y * h));

        return result;
    }

    private static bool IsUnitRange(IReadOnlyList<double> points)
    {
        foreach (double value in points)
        {
            if (double.IsNaN(value) || value < -0.001 || value > 1.001) return false;
        }

        return true;
    }

    private static double Clamp01(double value)
    {
        if (double.IsNaN(value)) return 0;
        return Math.Clamp(value, 0, 1);
    }
}