using InkCanvas.AnalyticGeometry.Algebra;
using InkCanvas.AnalyticGeometry;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class MathMarkupTests
{
    [Theory]
    [InlineData("y=a*x^2+b*x+c", "y=a*(x)^(2)+b*x+c")]
    [InlineData("y=sin(x)/2", "y=(sin(x))/(2)")]
    [InlineData("x^2/9-y^2/4=1", "((x)^(2))/(9)-((y)^(2))/(4)=1")]
    [InlineData("y=sqrt(abs(x))", "y=sqrt(abs(x))")]
    [InlineData("x=2π*y", "x=2*pi*y")]
    public void ExpressionAndMathMlRoundTrip(string input, string expected)
    {
        string mathMl = MathMarkup.ToMathML(input);
        Assert.Contains("<math", mathMl);
        if (input.Contains('/')) Assert.Contains("<mfrac", mathMl, StringComparison.Ordinal);
        Assert.Equal(expected, MathMarkup.ToExpression(mathMl));
        Assert.NotNull(CompiledPlot.Compile(new PlotDefinition { Formula = expected }));
    }

    [Fact]
    public void NativeMathMlFractionsAndPowerBecomePlotExpression()
    {
        const string mathMl = "<math xmlns='http://www.w3.org/1998/Math/MathML'><mrow><mi>y</mi><mo>=</mo><mfrac><msup><mi>x</mi><mn>2</mn></msup><mn>4</mn></mfrac></mrow></math>";
        Assert.Equal("y=((x)^(2))/(4)", MathMarkup.ToExpression(mathMl));
    }

    [Theory]
    [InlineData("<!DOCTYPE math [<!ENTITY x SYSTEM 'file:///C:/windows/win.ini'>]><math>&x;</math>")]
    [InlineData("<math><mroot><mi>x</mi></mroot></math>")]
    public void RejectsUnsafeOrMalformedMathMl(string mathMl) => Assert.ThrowsAny<Exception>(() => MathMarkup.ToExpression(mathMl));

    [Theory]
    [InlineData("f(x)=x^2/(1+x)")]
    [InlineData("A=(1,2,3)")]
    [InlineData("[[1,2],[3,4]]")]
    [InlineData("Solve(x^2=2,x)")]
    [InlineData("x_1^2+α")]
    [InlineData("If(x>0,x,-x)")]
    public void WorkbenchNotationRetainsItsMeaning(string source)
    {
        var markup = MathMarkup.ToMathML(source);
        Assert.NotEmpty(MathMarkup.ToExpression(markup));
        if (source.Contains("[[")) Assert.Contains("<mtable", markup);
        if (source.Contains('_')) Assert.Contains("<msub", markup);
    }

    [Theory]
    [InlineData("Integral(x^2,x)", "∫")]
    [InlineData("Derivative(sin(x),x)", "mfrac")]
    public void CalculusShowsStructuralNotation(string input, string expected)
    {
        var markup = MathMarkup.ToMathML(input);
        Assert.Contains(expected, markup);
        Assert.NotEmpty(MathMarkup.ToExpression(markup));
    }

    [Fact]
    public void ExponentAppliesToTheSubscriptedVariable()
    {
        var markup = MathMarkup.ToMathML("x_1^2");
        Assert.Contains("<msup><msub>", markup);
        Assert.Equal("(x_1)^(2)", MathMarkup.ToExpression(markup));
    }

    [Theory]
    [InlineData("If(x<=0,-x,x)", "If(x<=0,-x,x)")]
    [InlineData("x!=2", "x!=2")]
    [InlineData("x>=1", "x>=1")]
    [InlineData("45°", "45°")]
    public void RelationsAndDegreesRetainTheirMeaning(string expression, string expected) =>
        Assert.Equal(expected, MathMarkup.ToExpression(MathMarkup.ToMathML(expression)));

    [Fact]
    public void FractionTemplateWrapsSelectionAndSelectsDenominator()
    {
        var edit = FormulaEditing.Apply("y=x+1", 2, 3, "分数");
        Assert.Equal("y=(x+1)/(□)", edit.Text);
        Assert.Equal('□', edit.Text[edit.SelectionStart]);
        Assert.Equal(1, edit.SelectionLength);
        var filled = FormulaEditing.Apply(edit.Text, edit.SelectionStart, edit.SelectionLength, "2");
        Assert.Equal("y=(x+1)/(2)", filled.Text);
    }

    [Fact]
    public void RootTemplateInsertsAtCaretAndEditsAsOneAction()
    {
        var edit = FormulaEditing.Apply("y=", 2, 0, "平方根");
        Assert.Equal("y=sqrt(□)", edit.Text);
        var filled = FormulaEditing.Apply(edit.Text, edit.SelectionStart, edit.SelectionLength, "x");
        Assert.Equal("y=sqrt(x)", filled.Text);
        Assert.NotEmpty(MathMarkup.ToMathML(filled.Text));
    }

    [Fact]
    public void BackspaceKeepsSurrogatePairsIntactAndEnforcesMaximumLength()
    {
        Assert.Equal("ab", FormulaEditing.Apply("a😀b", 3, 0, "⌫").Text);
        Assert.Throws<FormatException>(() => FormulaEditing.Apply("abcd", 4, 0, "1", 4));
    }

    [Theory]
    [InlineData("上标", "(x+1)^(□)")]
    [InlineData("下标", "(x+1)_(□)")]
    [InlineData("Integral()", "Integral(x+1,x)")]
    [InlineData("pow", "pow(x+1,□)")]
    public void StructuralKeysPreserveSelectedExpressions(string key, string expected) =>
        Assert.Equal(expected, FormulaEditing.Apply("x+1", 0, 3, key).Text);
}
