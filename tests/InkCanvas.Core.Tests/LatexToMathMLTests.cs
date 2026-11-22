using System.Xml.Linq;
using InkCanvas.AnalyticGeometry.Algebra;
using InkCanvas.Core.Latex;
using Xunit;

namespace InkCanvas.Core.Tests;

/// <summary>
/// LatexToMathML 纯逻辑测试：覆盖分式、上下标、嵌套根号、矩阵、方程组、
/// 希腊字母、大型运算符以及非法输入与未知命令保留策略。
/// </summary>
public sealed class LatexToMathMLTests
{
    private static readonly XNamespace Math = "http://www.w3.org/1998/Math/MathML";

    private static XElement Convert(string latex)
    {
        Assert.True(LatexToMathML.TryConvert(latex, out string mathMl, out string error), error);
        Assert.False(string.IsNullOrWhiteSpace(mathMl));
        XElement root = XDocument.Parse(mathMl).Root;
        Assert.NotNull(root);
        Assert.Equal("math", root.Name.LocalName);
        Assert.Equal(Math.NamespaceName, root.Name.NamespaceName);
        Assert.Equal("block", (string)root.Attribute("display"));
        return root;
    }

    private static XElement Single(XElement root, string localName)
        => Assert.Single(root.Descendants(Math + localName));

    [Fact]
    public void Fraction_ProducesMfracWithNumeratorAndDenominator()
    {
        XElement root = Convert(@"\frac{a}{b}");
        XElement fraction = Single(root, "mfrac");
        Assert.Equal(2, fraction.Elements().Count());
        Assert.Equal("a", fraction.Elements().First().Value);
        Assert.Equal("b", fraction.Elements().Last().Value);
    }

    [Fact]
    public void Superscript_ProducesMsup()
    {
        XElement root = Convert("x^{2}");
        XElement power = Single(root, "msup");
        Assert.Equal("x", power.Elements().First().Value);
        Assert.Equal("2", power.Elements().Last().Value);
    }

    [Fact]
    public void Subscript_ProducesMsub()
    {
        XElement root = Convert("x_{i}");
        XElement sub = Single(root, "msub");
        Assert.Equal("x", sub.Elements().First().Value);
        Assert.Equal("i", sub.Elements().Last().Value);
    }

    [Fact]
    public void SubAndSuperscript_ProducesMsubsup()
    {
        XElement root = Convert("x_{i}^{2}");
        XElement both = Single(root, "msubsup");
        Assert.Equal(3, both.Elements().Count());
        Assert.Equal("x", both.Elements().ElementAt(0).Value);
        Assert.Equal("i", both.Elements().ElementAt(1).Value);
        Assert.Equal("2", both.Elements().ElementAt(2).Value);
    }

    [Fact]
    public void NestedSquareRootInsideFraction_IsPreserved()
    {
        XElement root = Convert(@"\frac{\sqrt{x}}{2}");
        XElement fraction = Single(root, "mfrac");
        Assert.Equal("mfrac", fraction.Name.LocalName);
        XElement radicand = Assert.Single(fraction.Descendants(Math + "msqrt"));
        Assert.Equal("x", radicand.Value);

        // MathFormulaPreview 的读取器必须接受该结构（与预览端覆盖范围互通）。
        string expression = MathMarkup.ToExpression(root.ToString(SaveOptions.DisableFormatting));
        Assert.Contains("sqrt(x)", expression);
    }

    [Fact]
    public void IndexedRoot_ProducesMrootWithIndex()
    {
        XElement root = Convert(@"\sqrt[3]{x}");
        XElement radical = Single(root, "mroot");
        Assert.Equal(2, radical.Elements().Count());
        Assert.Equal("x", radical.Elements().First().Value);
        Assert.Equal("3", radical.Elements().Last().Value);
    }

    [Fact]
    public void Matrix_ProducesTableWithRowsAndCells()
    {
        XElement root = Convert(@"\begin{matrix} a & b \\ c & d \end{matrix}");
        XElement table = Single(root, "mtable");
        Assert.Equal(2, table.Elements(Math + "mtr").Count());
        Assert.Equal(4, table.Descendants(Math + "mtd").Count());
        Assert.Equal(new[] { "a", "b", "c", "d" },
            table.Descendants(Math + "mtd").Select(cell => cell.Value).ToArray());
    }

    [Fact]
    public void CasesEnvironment_ProducesFencedTable()
    {
        XElement root = Convert(@"\begin{cases} x = 1 \\ y = 2 \end{cases}");
        XElement fenced = Single(root, "mfenced");
        Assert.Equal("{", (string)fenced.Attribute("open"));
        XElement table = Assert.Single(fenced.Descendants(Math + "mtable"));
        Assert.Equal(2, table.Elements(Math + "mtr").Count());
    }

    [Fact]
    public void GreekLettersAndOperators_MapToUnicode()
    {
        XElement root = Convert(@"\alpha + \beta = \pi \cdot 2 \leq \infty");
        var atoms = root.Descendants()
            .Where(node => node.Name.LocalName is "mi" or "mo")
            .Select(node => node.Value)
            .ToList();

        Assert.Contains("α", atoms);
        Assert.Contains("β", atoms);
        Assert.Contains("π", atoms);
        Assert.Contains("⋅", atoms);
        Assert.Contains("≤", atoms);
        Assert.Contains("∞", atoms);
    }

    [Fact]
    public void BigOperatorWithLimits_ProducesMunderover()
    {
        XElement root = Convert(@"\sum_{i=1}^{n} i");
        XElement limits = Single(root, "munderover");
        Assert.Equal("∑", limits.Elements().First().Value);
        Assert.Equal("i=1", limits.Elements().ElementAt(1).Value);
        Assert.Equal("n", limits.Elements().ElementAt(2).Value);

        XElement integral = Convert(@"\int_{0}^{1} x dx");
        Assert.Single(integral.Descendants(Math + "munderover"));
    }

    [Fact]
    public void LeftRight_ProducesMfenced()
    {
        XElement root = Convert(@"\left( \frac{a}{b} \right)");
        XElement fenced = Single(root, "mfenced");
        Assert.Equal("(", (string)fenced.Attribute("open"));
        Assert.Equal(")", (string)fenced.Attribute("close"));
        Assert.Single(fenced.Descendants(Math + "mfrac"));
    }

    [Fact]
    public void UnknownCommand_IsKeptAsTextAndReported()
    {
        bool ok = LatexToMathML.TryConvert(
            @"\foo{x}", out string mathMl, out string error, out IReadOnlyList<string> warnings);

        Assert.True(ok, error);
        Assert.Empty(error);
        Assert.NotEmpty(warnings);
        Assert.Contains("\\foo", XDocument.Parse(mathMl).Root.Descendants(Math + "mtext").First().Value);
        Assert.Contains("\\foo", warnings[0]);
    }

    [Theory]
    [InlineData("", "空")]
    [InlineData("   ", "空")]
    [InlineData(@"\frac{a}", "参数")]
    [InlineData("{a", "未闭合")]
    [InlineData("a}", "括号")]
    [InlineData(@"\left( x", "right")]
    [InlineData(@"\begin{foo} a \end{foo}", "环境")]
    [InlineData(@"\begin{matrix} a", "end")]
    public void InvalidInput_ReturnsFalseWithoutThrowing(string latex, string expectedFragment)
    {
        bool ok = LatexToMathML.TryConvert(latex, out string mathMl, out string error);

        Assert.False(ok);
        Assert.Empty(mathMl);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Contains(expectedFragment, error);
    }

    [Fact]
    public void ExcessiveNesting_IsRejected()
    {
        string latex = new string('{', 60) + "x" + new string('}', 60);
        Assert.False(LatexToMathML.TryConvert(latex, out _, out string error));
        Assert.Contains("嵌套", error);
    }

    [Fact]
    public void NestedFractionsAndScripts_RoundTripThroughPreviewReader()
    {
        XElement root = Convert(@"\frac{\sqrt{x_{1}}}{2^{n}} + x_{i}^{2}");
        string expression = MathMarkup.ToExpression(root.ToString(SaveOptions.DisableFormatting));
        Assert.Contains("sqrt(x_1)", expression);
    }
}