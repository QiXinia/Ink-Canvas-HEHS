using System.IO.Compression;
using System.Numerics;
using InkCanvas.AnalyticGeometry;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using InkCanvas.Features.Persistence;
using InkCanvas.Ink;
using InkCanvas.Pdf;
using InkCanvas.Platform.Windows;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class AnalyticGeometryTests
{
    [Theory]
    [InlineData("-2^2", -4)][InlineData("2^3^2", 512)][InlineData("2x+3(x-1)", 7)]
    [InlineData("sin(pi/2)+cos(0)", 2)][InlineData("log(1000)", 3)][InlineData("ln(exp(2))", 2)]
    [InlineData("sqrt(9)+abs(-3)", 6)][InlineData("max(2,min(3,4))", 3)][InlineData("x²+y²", 13)]
    [InlineData("1e-3x", .002)][InlineData("xy", 6)][InlineData("pow(2,3)", 8)]
    public void ArithmeticGrammarPreservesPrecedenceUnicodeAndImplicitProducts(string expression, double expected)
        => Assert.Equal(expected, MathExpression.Compile(expression).Evaluate(2, 3, 0, 1, 0, 0), 9);

    [Theory]
    [InlineData("File.Delete(x)")][InlineData("sin x")][InlineData("a=1")][InlineData("(x+1")][InlineData("x;")]
    [InlineData("1e999")][InlineData("max(1)")][InlineData("x..2")]
    public void InvalidAndExecutableInputsAreRejected(string input) => Assert.Throws<FormatException>(() => MathExpression.Compile(input));

    [Fact]
    public void DeepAndLongExpressionsHaveBoundedCompilation()
    {
        Assert.Throws<FormatException>(() => MathExpression.Compile(new string('(', 100) + "x" + new string(')', 100)));
        Assert.Throws<FormatException>(() => MathExpression.Compile(string.Join("+", Enumerable.Repeat("x", 600))));
    }
    [Theory]
    [InlineData("y=x^2", CompiledPlotKind.FunctionY)][InlineData("x=y^2", CompiledPlotKind.FunctionX)]
    [InlineData("x^2+y^2=25", CompiledPlotKind.Implicit)][InlineData("x^2+y^2-25", CompiledPlotKind.Implicit)]
    public void EquationsChooseTheCorrectCoordinateSolver(string input, CompiledPlotKind kind)
        => Assert.Equal(kind, CompiledPlot.Compile(new PlotDefinition { Formula = input }).Kind);

    [Theory]
    [InlineData("y=x^2")][InlineData("y=sin(x)")][InlineData("y=sqrt(x)")][InlineData("x=y^2")]
    public void CurvesAreFiniteClippedAndSampledWithinBudget(string input)
    {
        var definition = new PlotDefinition { Formula = input }; var mesh = PlotSampler.Sample(CompiledPlot.Compile(definition), definition);
        Assert.NotEmpty(mesh.Paths); Assert.InRange(mesh.Evaluations, 1, PlotSampler.MaximumEvaluations);
        Assert.InRange(mesh.Paths.Sum(path => path.Length), 2, PlotSampler.MaximumPoints);
        foreach (var point in mesh.Paths.SelectMany(path => path))
        { Assert.InRange(point.X, -.001, PlotSampler.Width + .001); Assert.InRange(point.Y, -.001, PlotSampler.Height + .001); }
    }
    [Fact]
    public void ReciprocalNeverJoinsAcrossItsAsymptote()
    {
        var definition = new PlotDefinition { Formula = "y=1/x" }; var mesh = PlotSampler.Sample(CompiledPlot.Compile(definition), definition);
        Assert.True(mesh.Paths.Count >= 2);
        foreach (var path in mesh.Paths)
        { Assert.True(path.All(point => point.X < PlotSampler.Width / 2) || path.All(point => point.X > PlotSampler.Width / 2)); }
    }
    [Theory]
    [InlineData("x^2+y^2=25", 1)][InlineData("x^2/9-y^2/4=1", 2)]
    public void ImplicitContoursStitchIntoClosedOrSeparateBranches(string formula, int minimum)
    {
        var definition = new PlotDefinition { Formula = formula }; var mesh = PlotSampler.Sample(CompiledPlot.Compile(definition), definition);
        var compiled = CompiledPlot.Compile(definition);
        Assert.InRange(mesh.Paths.Count, minimum, minimum + 3);
        foreach (var point in mesh.Paths.SelectMany(path => path))
        {
            double x = definition.XMin + point.X / PlotSampler.Width * (definition.XMax - definition.XMin);
            double y = definition.YMax - point.Y / PlotSampler.Height * (definition.YMax - definition.YMin);
            Assert.InRange(Math.Abs(compiled.First.Evaluate(x, y, 0, 1, 0, 0)), 0, .001);
        }
    }
    [Fact]
    public void ImplicitPoleDoesNotBecomeAFalseZeroContour()
    {
        var definition = new PlotDefinition { Formula = "1/x=0" };
        Assert.Empty(PlotSampler.Sample(CompiledPlot.Compile(definition), definition).Paths);
    }
    [Theory]
    [InlineData(PlotKind.Parametric, "5*cos(t)", "3*sin(t)")][InlineData(PlotKind.Polar, "5*cos(3*t)", "")]
    public void ParametricAndPolarCurvesProduceConnectedShapes(PlotKind kind, string first, string second)
    {
        var definition = new PlotDefinition { Kind = kind, Formula = first, SecondFormula = second };
        var mesh = PlotSampler.Sample(CompiledPlot.Compile(definition), definition); Assert.Single(mesh.Paths);
        var path = mesh.Paths[0]; Assert.True(Math.Abs(path[0].X - path[^1].X) < .001 && Math.Abs(path[0].Y - path[^1].Y) < .001);
    }
    [Fact]
    public void CanceledAndAdversarialPlotsRemainBounded()
    {
        var definition = new PlotDefinition { Formula = "y=sin(10000*x)" }; var compiled = CompiledPlot.Compile(definition);
        Assert.ThrowsAny<OperationCanceledException>(() => PlotSampler.Sample(compiled, definition, new CancellationToken(true)));
        var mesh = PlotSampler.Sample(compiled, definition);
        Assert.True(mesh.Evaluations <= PlotSampler.MaximumEvaluations && mesh.Paths.Sum(path => path.Length) <= PlotSampler.MaximumPoints);
    }
    private static InkStroke Contour(float y, ShapeSource source)
    {
        var stroke = new InkStroke(new StrokeAttributes { TaperKind = TaperKind.None, BaseWidth = 3 }) { ShapeSource = source, IsRecognizedShape = true };
        stroke.AddPoint(10, y); stroke.AddPoint(110, y); stroke.Complete();
        stroke.ObjectFrame = new SelectionFrame(new Rect(0, 0, 120, 80), Matrix3x2.Identity); return stroke;
    }
    [Fact]
    public void PickingAndControlDraggingAContourSelectsAndClonesTheWholeFormulaShape()
    {
        using var controller = new InkController { Tool = InkTool.Select };
        var source = new ShapeSource(Guid.NewGuid(), "inkcanvas.analytic-geometry", "{\"Formula\":\"y=x\"}");
        controller.Document.AddRange(new[] { Contour(20, source), Contour(60, source) });
        var pick = new OverlayPointer(1, 50, 20, .5f, 1, 1, OverlayPointerKind.Mouse, 1);
        controller.OnPointerDown(pick); controller.OnPointerUp(pick); Assert.Equal(2, controller.Selection.Count);
        var drag = pick with { X = 60, Y = 40, Control = true };
        controller.OnPointerDown(drag); controller.OnPointerMove(drag with { X = 260, Y = 140 }); controller.OnPointerUp(drag with { X = 260, Y = 140 });
        Assert.Equal(4, controller.Document.Count); Assert.Equal(2, controller.Document.Strokes.Select(stroke => stroke.ShapeSource.GroupId).Distinct().Count());
        controller.Undo(); Assert.Equal(2, controller.Document.Count); controller.Redo(); Assert.Equal(4, controller.Document.Count);
    }
    [Fact]
    public void FormulaMetadataSurvivesInkAndPdfPersistence()
    {
        string path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".icink");
        var source = new ShapeSource(Guid.NewGuid(), "inkcanvas.analytic-geometry", "{\"Formula\":\"x^2+y^2=25\"}");
        var original = Contour(20, source); original.Transform(Matrix3x2.CreateRotation(.3f));
        try
        {
            InkFileStore.Save(path, new[] { original }); var loaded = Assert.Single(InkFileStore.Load(path));
            Assert.Equal(source, loaded.ShapeSource); Assert.Equal(original.ExportObjectFrame(), loaded.ExportObjectFrame());
            var pdf = Assert.Single(PdfInkCodec.Decode(PdfInkCodec.Encode(new[] { original })));
            Assert.Equal(source, pdf.ShapeSource); Assert.Equal(original.Points, pdf.Points);
        }
        finally { File.Delete(path); }
    }
    [Theory]
    [InlineData(LayerAction.MoveForward)][InlineData(LayerAction.MoveBackward)]
    public void LayerStepsKeepFormulaContoursTogether(LayerAction action)
    {
        using var controller = new InkController { Tool = InkTool.Select };
        var first = new ShapeSource(Guid.NewGuid(), "inkcanvas.analytic-geometry", "{}");
        var second = new ShapeSource(Guid.NewGuid(), "inkcanvas.analytic-geometry", "{}");
        var a = new[] { Contour(10, first), Contour(20, first) }; var b = new[] { Contour(30, second), Contour(40, second) };
        foreach (var stroke in a) stroke.LayerOrder = 0;
        foreach (var stroke in b) stroke.LayerOrder = 1;
        controller.Document.AddRange(a.Concat(b)); controller.Selection.AddRange(action == LayerAction.MoveForward ? a : b);
        controller.ArrangeSelection(action);
        Assert.Equal(new[] { second.GroupId, second.GroupId, first.GroupId, first.GroupId }, controller.DrawingObjects.Select(item => item.Stroke.ShapeSource.GroupId));
        controller.Undo(); Assert.Equal(new[] { first.GroupId, first.GroupId, second.GroupId, second.GroupId }, controller.DrawingObjects.Select(item => item.Stroke.ShapeSource.GroupId));
        controller.Redo(); Assert.Equal(new[] { second.GroupId, second.GroupId, first.GroupId, first.GroupId }, controller.DrawingObjects.Select(item => item.Stroke.ShapeSource.GroupId));
    }
    [Fact]
    public void LegacyPdfInkWithoutFormulaMetadataStillLoads()
    {
        using var memory = new MemoryStream();
        using (var zlib = new ZLibStream(memory, CompressionLevel.Optimal, true))
        using (var writer = new BinaryWriter(zlib))
        {
            writer.Write(0x314B4349); writer.Write(1); writer.Write(Guid.NewGuid().ToByteArray()); writer.Write(false);
            writer.Write(false); writer.Write(InkColor.Black.PackedArgb); writer.Write(3f); writer.Write(false); writer.Write((byte)96);
            writer.Write((int)TaperKind.None); writer.Write((int)PenNib.Round); writer.Write(0f); writer.Write(false); writer.Write(1);
            foreach (float value in new[] { 10f, 10f, .5f, 3f, 0f }) writer.Write(value);
        }
        Assert.Null(Assert.Single(PdfInkCodec.Decode(memory.ToArray())).ShapeSource);
    }
}
