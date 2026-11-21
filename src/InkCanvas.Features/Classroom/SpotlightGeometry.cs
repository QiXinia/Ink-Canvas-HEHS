namespace InkCanvas.Features.Classroom;

/// <summary>聚光灯亮区形状。</summary>
public enum SpotlightShape
{
    Circle,
    Rect,
}

/// <summary>聚光灯纯几何计算，供窗口与单元测试共用（不依赖任何 UI 类型）。</summary>
public static class SpotlightGeometry
{
    /// <summary>
    /// 把亮区中心钳制在画布内，并保证半径为 <paramref name="radius"/> 的亮区不会越界。
    /// 当半径大于可用半尺寸时，中心回到画布正中。
    /// </summary>
    public static (double X, double Y) ClampCenter(
        double centerX, double centerY, double width, double height, double radius)
    {
        if (width <= 0 || height <= 0) return (0, 0);

        double clampedRadius = ClampRadius(radius, width, height);
        double minX = clampedRadius;
        double maxX = width - clampedRadius;
        double minY = clampedRadius;
        double maxY = height - clampedRadius;

        double x = maxX >= minX ? Math.Clamp(centerX, minX, maxX) : width / 2.0;
        double y = maxY >= minY ? Math.Clamp(centerY, minY, maxY) : height / 2.0;
        return (x, y);
    }

    /// <summary>把半径钳制到 [0, min(width,height)/2]。</summary>
    public static double ClampRadius(double radius, double width, double height)
        => Math.Clamp(radius, 0, Math.Max(0, Math.Min(width, height) / 2.0));

    /// <summary>由直径求半径。</summary>
    public static double RadiusFrom(double diameter) => Math.Max(0, diameter) / 2.0;

    /// <summary>由半径求直径。</summary>
    public static double DiameterFrom(double radius) => Math.Max(0, radius) * 2.0;

    /// <summary>把任意两个对角点归一化为正的 Left/Top/Width/Height。</summary>
    public static (double Left, double Top, double Width, double Height) NormalizeRect(
        double x1, double y1, double x2, double y2)
    {
        double left = Math.Min(x1, x2);
        double top = Math.Min(y1, y2);
        return (left, top, Math.Abs(x2 - x1), Math.Abs(y2 - y1));
    }
}