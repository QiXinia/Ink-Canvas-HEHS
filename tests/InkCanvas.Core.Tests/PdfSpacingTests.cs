using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class PdfSpacingTests
{
    private static readonly PdfPageGeometry Page = new(0, 0, 300, 450, 0);

    [Fact]
    public void ColumnsShiftIndependentlyAndDeletingAGuideDeletesTheAssociatedAnswers()
    {
        var guide = new PdfColumnGuide(Guid.NewGuid(), 200);
        var left = new PdfBlankBand(Guid.NewGuid(), null, guide.Id, 180, 90);
        var right = new PdfBlankBand(Guid.NewGuid(), guide.Id, null, 360, 40);
        var spacing = new PdfPageSpacing([guide], [left, right]);
        Assert.True(spacing.IsValid(Page));
        Assert.Equal(690, spacing.Expand(Page).DipHeight, 3);
        Assert.Equal(new Vector2(100, 490), spacing.MapSource(Page, new(100, 400)));
        Assert.Equal(new Vector2(300, 440), spacing.MapSource(Page, new(300, 400)));
        var line = Stroke(100, 120, 100, 280, layer: 2);
        var crossingColumns = Stroke(100, 250, 300, 250);
        var unaffected = Stroke(300, 210, 300, 250);
        var shifted = PdfPageSpacing.RemapInk(Page, PdfPageSpacing.Empty, spacing, [line, crossingColumns, unaffected]);
        var vertical = shifted.Where(stroke => stroke.InkLayerIndex == 2).ToArray();
        Assert.Equal(2, vertical.Length);
        Assert.InRange(vertical[0].Points[^1].Y, 179.9f, 180);
        Assert.Equal(270, vertical[1].Points[0].Y, 3);
        Assert.Equal(370, vertical[1].Points[^1].Y, 3);
        Assert.Contains(shifted, stroke => stroke.Points[0].Y == 340 && stroke.Points[^1].X < 201);
        Assert.Contains(shifted, stroke => stroke.Points[0].Y == 250 && stroke.Points[^1].X == 300);
        Assert.Contains(unaffected, shifted);
        var answer = Stroke(80, 205, 130, 217);
        var after = spacing.Remove(guide.Id);
        Assert.True(after.IsEmpty);
        var restored = PdfPageSpacing.RemapInk(Page, spacing, after, [..shifted, answer]);
        Assert.DoesNotContain(restored, stroke => stroke.Points.Any(point => point.X is 80 or 130));
        Assert.Contains(restored, stroke => stroke.Points[^1].Position == new Vector2(100, 280));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(90, 0)]
    [InlineData(0, 90)]
    public async Task VectorPdfReopensWithEditableFootSpaceAndOutsideInk(int rotation, int turn)
    {
        string root = Directory.CreateTempSubdirectory("inkcanvas-pdf-spacing-").FullName;
        try
        {
            string source = Path.Combine(root, "source.pdf"), output = Path.Combine(root, "spaced.pdf"), restored = Path.Combine(root, "restored.pdf");
            using (var pdf = new PdfDocument())
            {
                var page = pdf.AddPage(); page.Width = XUnit.FromPoint(300); page.Height = XUnit.FromPoint(450); page.Rotate = rotation;
                var content = new PdfDictionary(pdf);
                content.CreateStream("0 0.25 1 rg 48 90 36 24 re f"u8.ToArray());
                pdf.Internals.AddObject(content);
                page.Elements["/Contents"] = content.Reference;
                pdf.Save(source);
            }
            using var input = await PdfAnnotationFile.OpenAsync(source);
            var geometry = input.Pages[0];
            var layout = new PdfPageSpacing([], [new(Guid.NewGuid(), null, null, geometry.DipHeight, 100)]);
            var ink = new[] { Stroke(40, geometry.DipHeight + 30, 90, geometry.DipHeight + 60), Stroke(-45, 60, 28, 65, 1) };
            var inkPages = new Dictionary<int, IReadOnlyList<InkStroke>> { [0] = ink };
            await input.SaveAsync(output, inkPages, new Dictionary<int, int> { [0] = turn }, new Dictionary<int, PdfPageSpacing> { [0] = layout });
            using (var pdf = PdfReader.Open(output, PdfDocumentOpenMode.Import))
            {
                var page = pdf.Pages[0];
                Assert.Equal((rotation + turn) % 360, page.Rotate);
                Assert.True(page.MediaBox.Height * page.MediaBox.Width > 300 * 450);
                var resources = page.Elements.GetDictionary("/Resources").Elements.GetDictionary("/XObject");
                Assert.Equal("/Form", resources.Elements.GetDictionary("/InkCanvasPage").Elements.GetName("/Subtype"));
            }
            using var reopened = await PdfAnnotationFile.OpenAsync(output);
            Assert.Equal(geometry, reopened.Pages[0]);
            Assert.Equal(turn, reopened.GetDisplayRotation(0));
            Assert.Equal(layout.Bands, reopened.GetSpacing(0).Bands);
            var captured = reopened.GetInk(0);
            Assert.Equal(2, captured.Count);
            Assert.InRange(captured.Single(s => s.InkLayerIndex == 1).Points[0].X, -45.01f, -44.99f);
            var remaining = PdfPageSpacing.RemapInk(geometry, layout, PdfPageSpacing.Empty, captured);
            Assert.Equal(1, Assert.Single(remaining).InkLayerIndex);
            await reopened.SaveAsync(restored, new Dictionary<int, IReadOnlyList<InkStroke>> { [0] = remaining },
                new Dictionary<int, int>(), new Dictionary<int, PdfPageSpacing>());
            using var final = await PdfAnnotationFile.OpenAsync(restored);
            Assert.True(final.GetSpacing(0).IsEmpty);
            Assert.Equal(geometry.DipHeight, final.Pages[0].DipHeight, 2);
            Assert.Single(final.GetInk(0));
        }
        finally { Directory.Delete(root, true); }
    }

    private static InkStroke Stroke(float x1, float y1, float x2, float y2, int layer = 0)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 4, TaperKind = TaperKind.None }) { InkLayerIndex = layer };
        stroke.AddPoint(x1, y1);
        stroke.AddPoint(x2, y2);
        stroke.Complete();
        return stroke;
    }
}
