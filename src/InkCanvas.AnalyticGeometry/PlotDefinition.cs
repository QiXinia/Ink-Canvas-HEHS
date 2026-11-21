namespace InkCanvas.AnalyticGeometry;

public enum PlotKind { Cartesian, Parametric, Polar }

public sealed record PlotDefinition
{
    public PlotKind Kind { get; init; }
    public string Formula { get; init; } = "y=a*x^2+b*x+c";
    public string SecondFormula { get; init; } = "sin(t)";
    public string FormulaMathML { get; init; }
    public string SecondFormulaMathML { get; init; }
    public double XMin { get; init; } = -10;
    public double XMax { get; init; } = 10;
    public double YMin { get; init; } = -7.5;
    public double YMax { get; init; } = 7.5;
    public double TMin { get; init; }
    public double TMax { get; init; } = Math.Tau;
    public double A { get; init; } = 1;
    public double B { get; init; }
    public double C { get; init; }
    public bool IncludeAxes { get; init; } = true;

    public void Validate()
    {
        if (!Enum.IsDefined(Kind)) throw new FormatException("请选择有效的坐标类型。");
        if (string.IsNullOrWhiteSpace(Formula) || Formula.Length > 2048 || SecondFormula?.Length > 2048)
            throw new FormatException("请输入公式，每个公式最多 2048 个字符。");
        if (FormulaMathML?.Length > 32768 || SecondFormulaMathML?.Length > 32768)
            throw new FormatException("MathML 公式超过允许长度。");
        if (new[] { XMin, XMax, YMin, YMax, TMin, TMax, A, B, C }.Any(value => !double.IsFinite(value) || Math.Abs(value) > 1e8))
            throw new FormatException("坐标和参数必须是绝对值不超过 10⁸ 的有限数值。");
        if (XMax - XMin < 1e-7 || YMax - YMin < 1e-7 || TMax - TMin < 1e-7)
            throw new FormatException("每个范围的最大值必须大于最小值。");
    }
}

public readonly record struct PlotPoint(double X, double Y);
public sealed record PlotMesh(IReadOnlyList<PlotPoint[]> Paths, int Evaluations, bool Limited);
