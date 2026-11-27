using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using InkCanvas.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class PdfPageEditingTests
{
    private static PdfPageGeometry Page(int rotation = 0) => new(20, 30, 620, 830, rotation);

    [Fact]
    public void ReorderedLayoutUsesSourceIdentityForRenderingAndPositionForNavigation()
    {
        var layout = new PdfContinuousLayout(Enumerable.Repeat(Page(), 6).ToArray(),
            doublePages: true, separateCover: true, pageOrder: [4, 1, 5, 2]);
        Assert.Equal(4, layout.Count);
        Assert.Equal(4, layout.PageAt(0));
        Assert.Equal(-1, layout.PositionOf(0));
        Assert.Equal(2, layout.PositionOf(5));
        Assert.Equal(4, layout.AdjacentPage(1, -1));
        Assert.Equal(2, layout.AdjacentPage(5, 1));
        Assert.Equal(5, layout.HitTest(layout[5].Center));
        Assert.Equal(new[] { 1, 5 }, layout.VisiblePages(layout[1].Top, layout[1].Bottom));
        Assert.Equal(new[] { 1, 5, 2, 4 }, layout.PreviewPages(layout[1].Top, layout[1].Bottom, 1));
    }

    [Theory]
    [InlineData(false, 0, 1)]
    [InlineData(true, 1, 2)]
    public void DoublePageRowsIncludeCoverAndHitBothPages(bool cover, int left, int right)
    {
        var layout = new PdfContinuousLayout(Enumerable.Repeat(Page(), 6).ToArray(), doublePages: true, separateCover: cover);
        Assert.Equal(layout[left].Top, layout[right].Top);
        Assert.Equal(24, layout[right].Left - layout[left].Right);
        Assert.Equal(left, layout.HitTest(layout[left].Center));
        Assert.Equal(right, layout.HitTest(layout[right].Center));
        Assert.Equal(-1, layout.HitTest(new((layout[left].Right + layout[right].Left) / 2, layout[left].Center.Y)));
        Assert.Contains(right, layout.VisiblePages(layout[left].Top, layout[left].Bottom));
        if (cover) Assert.True(layout[0].Bottom < layout[1].Top);
        Assert.Equal(cover ? 3 : 2, layout.AdjacentPage(right, 1));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void LayoutRotationKeepsCanonicalInkAndPdfCoordinates(int originalRotation)
    {
        var page = Page(originalRotation);
        var rotated = page with { Rotation = page.Rotation + 90 };
        var layout = new PdfContinuousLayout(new[] { page, Page() }, rotations: new[] { 90, 0 });
        var document = Matrix3x2.CreateScale(1.37f) * Matrix3x2.CreateTranslation(-50, -140);
        var local = new Vector2(123, 456);
        var changed = Vector2.Transform(local, page.TransformTo(rotated));
        Assert.True(Vector2.Distance(page.ToPdf(local), rotated.ToPdf(changed)) < .001f);
        Assert.True(Vector2.Distance(Vector2.Transform(changed + new Vector2(layout[0].Left, layout[0].Top), document),
            Vector2.Transform(local, layout.PageView(0, document))) < .001f);
        var restored = layout.DocumentView(0, layout.PageView(0, document));
        Assert.True(Vector2.Distance(document.Translation, restored.Translation) < .001f);
        Assert.Equal(document.GetDeterminant(), restored.GetDeterminant(), 4);
    }

    [Fact]
    public void CrossingStrokeBecomesDistinctEditableFragmentsWithInterpolatedPressure()
    {
        var layout = new PdfContinuousLayout(new[] { Page(), Page() });
        var stroke = Crossing(layout[0].Height);
        var fragments = PdfInkSplitter.Split(stroke, 0, layout);
        Assert.Equal(2, fragments.Count);
        var first = Assert.Single(fragments[0]); var second = Assert.Single(fragments[1]);
        Assert.NotEqual(first.Id, second.Id);
        Assert.Equal(layout[0].Height, first.Points[^1].Y, 3);
        Assert.Equal(0, second.Points[0].Y, 3);
        Assert.InRange(first.Points[^1].Width, 2.01f, 7.99f);
        Assert.InRange(second.Points[0].Pressure, .31f, .79f);
        Assert.True(first.Points.All(point => point.HasPressure));
        Assert.Equal(stroke.Attributes.HighlighterAlpha, second.Attributes.HighlighterAlpha);
        Assert.Equal(stroke.LayerOrder, second.LayerOrder);
        Assert.Single(first.Points, point => point.TimeMs == 0);
    }

    [Fact]
    public void ClippingARoundedRingAcrossFacingPagesPreservesTheHole()
    {
        var layout = new PdfContinuousLayout(new[] { Page(), Page() }, doublePages: true);
        float right = layout[0].Width;
        var construction = new ShapeDefinition
        {
            Kind = ShapeKind.RoundedRectangleRing, Parameters = [right - 110, 20, right + 50, 150, 28, 55],
            FillColor = 0xFF369AFF,
        };
        var fill = Assert.Single(construction.Generate(new(), Guid.NewGuid()), stroke => stroke.ShapeSource.Geometry.IsFill);
        var fragments = PdfInkSplitter.Split(fill, 0, layout);
        Assert.Equal(2, fragments.Count);
        var left = Assert.Single(fragments[0]);
        Assert.Equal(2, left.FillContours().Count());
        Assert.Equal(10, left.DistanceTo(new(right - 30, 85)), 3);
        Assert.Equal(ShapeConstruction.Contours, left.ShapeSource.Geometry.Construction);
    }

    [Fact]
    public void HorizontalStrokeRoutesToTheFacingPageAndMarginsKeepTheirIdentity()
    {
        var layout = new PdfContinuousLayout(new[] { Page(), Page() }, doublePages: true);
        var stroke = new InkStroke(new() { TaperKind = TaperKind.None });
        stroke.AddPoint(layout[0].Width - 20, 100); stroke.AddPoint(layout[0].Width + 44, 100); stroke.Complete();
        var fragments = PdfInkSplitter.Split(stroke, 0, layout);
        Assert.Equal(2, fragments.Count);
        Assert.Equal(0, fragments[1][0].Points[0].X, 3);
        var margin = new InkStroke(new()); margin.AddPoint(-20, 50); margin.AddPoint(20, 50); margin.Complete();
        Assert.Same(margin, Assert.Single(PdfInkSplitter.Split(margin, 0, layout)[0]));
    }

    [Fact]
    public void SplitHandlesRotatedPagesAndFilledShapeContours()
    {
        var page = Page();
        var layout = new PdfContinuousLayout(new[] { page, page }, rotations: new[] { 90, 270 });
        Matrix3x2.Invert(layout.PageView(0, Matrix3x2.Identity), out var toSource);
        var bounds = layout[0];
        var stroke = new InkStroke(new() { TaperKind = TaperKind.None });
        stroke.AddPoint(bounds.Center.X, bounds.Bottom - 20); stroke.AddPoint(bounds.Center.X, layout[1].Top + 30); stroke.Complete();
        stroke.Transform(toSource, scaleWidth: false);
        var parts = PdfInkSplitter.Split(stroke, 0, layout);
        Assert.Equal(2, parts.Count);
        foreach (var pair in parts)
        foreach (var fragment in pair.Value)
        foreach (var point in fragment.Points)
            Assert.True(new Rect(-.01f, -.01f, page.DipWidth + .01f, page.DipHeight + .01f).Contains(point.Position));

        var vertical = new PdfContinuousLayout(new[] { page, page });
        var filled = new InkStroke(new() { TaperKind = TaperKind.None });
        var vertices = new[] { new Vector2(100, page.DipHeight - 20), new Vector2(300, page.DipHeight - 20),
            new Vector2(300, page.DipHeight + 60), new Vector2(100, page.DipHeight + 60) };
        filled.RestorePoints(vertices.Select(point => new InkPoint(point.X, point.Y, .5f, 1, 0)));
        filled.ShapeSource = ShapeSource.FromGeometry(Guid.NewGuid(), new InkCanvas.Core.Shapes.ShapeDefinition
        {
            Kind = InkCanvas.Core.Shapes.ShapeKind.Rectangle, IsFill = true,
            Parameters = new[] { 100f, page.DipHeight - 20, 300, page.DipHeight + 60 },
            Vertices = vertices.SelectMany(point => new[] { point.X, point.Y }).ToArray(),
        });
        filled.Complete();
        var fills = PdfInkSplitter.Split(filled, 0, vertical);
        Assert.Equal(2, fills.Count);
        Assert.All(fills.Values.SelectMany(ink => ink), fragment =>
        {
            Assert.True(fragment.ShapeSource.Geometry.IsFill);
            Assert.True(fragment.Points.All(point => point.Y >= -.001f && point.Y <= page.DipHeight + .001f));
        });
    }

    [Fact]
    public void DoublePagePrefetchRemainsBoundedForLargeDocuments()
    {
        var layout = new PdfContinuousLayout(Enumerable.Repeat(Page(), 10000).ToArray(), doublePages: true, separateCover: true);
        var visible = layout.VisiblePages(layout[8001].Top, layout[8002].Bottom).ToArray();
        Assert.Equal(new[] { 8001, 8002 }, visible);
        var previews = layout.PreviewPages(layout[8001].Top, layout[8002].Bottom, 1).ToArray();
        Assert.Equal(previews.Length, previews.Distinct().Count());
        Assert.InRange(previews.Length, 4, 6);
    }

    [Fact]
    public async Task SavingSplitsInkWithoutGrowingPagesAndDeletingRemovesAllAppearances()
    {
        string directory = Path.Combine(Path.GetTempPath(), "InkCanvas-Pdf-Split-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source.pdf"), saved = Path.Combine(directory, "saved.pdf"), erased = Path.Combine(directory, "erased.pdf");
            CreatePdf(source);
            using (var file = await PdfAnnotationFile.OpenAsync(source))
                await file.SaveAsync(saved, new Dictionary<int, IReadOnlyList<InkStroke>> { [0] = new[] { Crossing(file.Pages[0].DipHeight) } });
            using (var file = await PdfAnnotationFile.OpenAsync(saved))
            {
                Assert.Equal(2, file.InkPages.Count());
                Assert.Equal(800 * 96f / 72, file.Pages[0].DipHeight, 3);
                Assert.NotEqual(Assert.Single(file.GetInk(0)).Id, Assert.Single(file.GetInk(1)).Id);
                await file.SaveAsync(erased, new Dictionary<int, IReadOnlyList<InkStroke>>());
            }
            using var empty = await PdfAnnotationFile.OpenAsync(erased);
            Assert.Empty(empty.InkPages);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(90)]
    [InlineData(-90)]
    public async Task RotationPersistsWithInkInTheSamePdfPosition(int turn)
    {
        string directory = Path.Combine(Path.GetTempPath(), "InkCanvas-Pdf-Rotate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string source = Path.Combine(directory, "source.pdf"), saved = Path.Combine(directory, "saved.pdf");
            CreatePdf(source);
            using var file = await PdfAnnotationFile.OpenAsync(source);
            var stroke = new InkStroke(new() { TaperKind = TaperKind.None });
            stroke.AddPoint(100, 200); stroke.AddPoint(250, 350); stroke.Complete();
            await file.SaveAsync(saved, new Dictionary<int, IReadOnlyList<InkStroke>> { [0] = new[] { stroke } },
                rotations: new Dictionary<int, int> { [0] = turn });
            using var reopened = await PdfAnnotationFile.OpenAsync(saved);
            Assert.Equal((turn + 360) % 360, reopened.Pages[0].NormalizedRotation);
            Assert.Equal(file.Pages[0].DipHeight, reopened.Pages[0].DipWidth, 3);
            var restored = Assert.Single(reopened.GetInk(0));
            Assert.True(Vector2.Distance(file.Pages[0].ToPdf(stroke.Points[0].Position),
                reopened.Pages[0].ToPdf(restored.Points[0].Position)) < .001f);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static InkStroke Crossing(float height)
    {
        var stroke = new InkStroke(new() { TaperKind = TaperKind.None, HighlighterAlpha = 70 }) { LayerOrder = 37 };
        stroke.RestorePoints(new[] { new InkPoint(200, height - 20, .3f, 2, 0, true, 12, 8),
            new InkPoint(220, height + 44, .8f, 8, 80, true, 18, 10) });
        stroke.Complete(); return stroke;
    }

    private static void CreatePdf(string path)
    {
        using var document = new PdfDocument();
        for (int index = 0; index < 2; index++)
        {
            var page = document.AddPage(); page.Width = XUnit.FromPoint(600); page.Height = XUnit.FromPoint(800);
        }
        document.Save(path);
    }
}
