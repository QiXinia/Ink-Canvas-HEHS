using System.Numerics;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using InkCanvas.Pdf;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;
using Xunit;

namespace InkCanvas.Core.Tests;

public sealed class PdfDocumentTests
{
    [Fact]
    public async Task OrganizingPagesPersistsNativeOrderRotationBookmarksAndEditableContent()
    {
        string root = Directory.CreateTempSubdirectory("inkcanvas-organize-pdf-").FullName;
        try
        {
            string path = Path.Combine(root, "original.pdf"), saved = Path.Combine(root, "organized.pdf");
            using (var pdf = new PdfDocument())
            {
                for (int i = 0; i < 4; i++)
                {
                    var page = pdf.AddPage();
                    page.Width = XUnit.FromPoint(360 + i * 30);
                    page.Height = XUnit.FromPoint(520);
                    pdf.Outlines.Add("Page " + i, page, true);
                }
                pdf.Save(path);
            }
            using var file = await PdfAnnotationFile.OpenAsync(path);
            var geometry = file.Pages[3];
            var band = new PdfBlankBand(Guid.NewGuid(), null, null, geometry.DipHeight, 80);
            var spacing = new PdfPageSpacing([], [band]);
            var ink = new InkStroke(new() { TaperKind = TaperKind.None }) { InkLayerIndex = 2 };
            ink.AddPoint(90, geometry.DipHeight + 15); ink.AddPoint(160, geometry.DipHeight + 35); ink.Complete();
            var pages = new Dictionary<int, IReadOnlyList<InkStroke>> { [3] = new[] { ink }, [1] = new[] { Stroke() } };
            await file.SaveAsync(saved, pages, new Dictionary<int, int> { [3] = 90 },
                new Dictionary<int, PdfPageSpacing> { [3] = spacing }, pageOrder: [3, 0, 2]);
            using (var standard = PdfReader.Open(saved, PdfDocumentOpenMode.Import))
            {
                Assert.Equal(3, standard.PageCount);
                Assert.Equal(90, standard.Pages[0].Rotate);
                Assert.Equal(360, standard.Pages[1].Width.Point, 3);
                Assert.Equal(420, standard.Pages[2].Width.Point, 3);
            }
            using var reopened = await PdfAnnotationFile.OpenAsync(saved);
            Assert.Equal(new[] { 1, 2, 0 }, reopened.Bookmarks.Select(bookmark => bookmark.PageIndex));
            Assert.DoesNotContain(reopened.Bookmarks, bookmark => bookmark.Title == "Page 1");
            Assert.Equal(band, Assert.Single(reopened.GetSpacing(0).Bands));
            var restored = Assert.Single(reopened.GetInk(0));
            Assert.Equal(ink.Id, restored.Id);
            Assert.Equal(2, restored.InkLayerIndex);
            Assert.Empty(reopened.GetInk(1));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RoundedRectangleRingExportsAHollowFillAndKeepsItsControlPoints()
    {
        string root = Directory.CreateTempSubdirectory("inkcanvas-rounded-ring-").FullName;
        try
        {
            string original = Path.Combine(root, "source.pdf"), exported = Path.Combine(root, "export.pdf");
            CreateFixture(original, pages: 1);
            var definition = new ShapeDefinition
            {
                Kind = ShapeKind.RoundedRectangleRing, Parameters = [20, 20, 220, 160, 25, 18], FillColor = 0xFF369AFF,
            };
            var strokes = definition.Generate(new(), Guid.NewGuid());
            var fill = Assert.Single(strokes, stroke => stroke.ShapeSource.Geometry.IsFill);
            Assert.Equal(2, fill.FillContours().Count());
            Assert.InRange(fill.DistanceTo(new(120, 90)), 51.9f, 52.1f);
            Assert.Equal(0, fill.DistanceTo(new(120, 29)));
            Assert.True(StrokeEraser.EraseDisc(fill, new(120, 29), 4, out var clipped));
            var erased = Assert.Single(clipped);
            Assert.True(erased.DistanceTo(new(120, 29)) > 3.9f);
            Assert.True(erased.DistanceTo(new(120, 90)) > 51);
            using (var file = await PdfAnnotationFile.OpenAsync(original))
                await file.SaveAsync(exported, new Dictionary<int, IReadOnlyList<InkStroke>> { [0] = strokes });
            using (var pdf = PdfReader.Open(exported, PdfDocumentOpenMode.Import))
            {
                var annotation = Assert.Single(pdf.Pages[0].Annotations.Cast<PdfAnnotation>(),
                    item => item.Elements.GetInteger("/InkCanvasVersion") == 1);
                var appearance = annotation.Elements.GetDictionary("/AP").Elements.GetDictionary("/N").Stream;
                string[] commands = System.Text.Encoding.ASCII.GetString(appearance.UnfilteredValue).Split('\n');
                int firstFill = Array.IndexOf(commands, "f");
                Assert.True(firstFill > 0);
                Assert.Equal(2, commands.Take(firstFill).Count(line => line == "h"));
            }
            using var reopened = await PdfAnnotationFile.OpenAsync(exported);
            var restored = Assert.Single(reopened.GetInk(0), stroke => stroke.ShapeSource.Geometry.IsFill);
            Assert.Equal(definition.ControlPoints(), restored.ShapeSource.Geometry.ControlPoints());
            Assert.Equal(fill.DistanceTo(new(120, 90)), restored.DistanceTo(new(120, 90)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AreaErasedFillKeepsTheHoleInThePdfAppearanceAndEditableInk()
    {
        string root = Directory.CreateTempSubdirectory("inkcanvas-erased-fill-").FullName;
        try
        {
            string original = Path.Combine(root, "source.pdf"), exported = Path.Combine(root, "export.pdf");
            CreateFixture(original, pages: 1);
            var shape = new ShapeDefinition
            {
                Kind = ShapeKind.Rectangle, Parameters = [20, 20, 180, 140],
                Vertices = [20, 20, 180, 20, 180, 140, 20, 140], FillColor = 0xFF369AFF,
            }.Generate(new StrokeAttributes { BaseWidth = 4 }, Guid.NewGuid());
            var center = new Vector2(100, 80);
            Assert.True(StrokeEraser.EraseDisc(shape[0], center, 25, out var clipped));
            using (var file = await PdfAnnotationFile.OpenAsync(original))
                await file.SaveAsync(exported, new Dictionary<int, IReadOnlyList<InkStroke>> { [0] = clipped });
            using (var pdf = PdfReader.Open(exported, PdfDocumentOpenMode.Import))
            {
                var annotation = Assert.Single(pdf.Pages[0].Annotations.Cast<PdfAnnotation>(),
                    item => item.Elements.GetInteger("/InkCanvasVersion") == 1);
                var appearance = annotation.Elements.GetDictionary("/AP").Elements.GetDictionary("/N").Stream;
                string[] commands = System.Text.Encoding.ASCII.GetString(appearance.UnfilteredValue).Split('\n');
                Assert.Equal(2, commands.Count(line => line == "h"));
                Assert.Single(commands, line => line == "f");
            }
            using var reopened = await PdfAnnotationFile.OpenAsync(exported);
            var restored = Assert.Single(reopened.GetInk(0));
            Assert.Equal(2, restored.FillContours().Count());
            Assert.InRange(restored.DistanceTo(center), 24.5f, 25);
            Assert.Equal(0, restored.DistanceTo(new Vector2(40, 80)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task LayeredInkExportsAsOneAppearanceAndReopensWithItsLayerMembership()
    {
        string root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string original = Path.Combine(root, "source.pdf"), exported = Path.Combine(root, "export.pdf");
            CreateFixture(original, pages: 1);
            var bottom = Stroke();
            bottom.LayerOrder = 90;
            bottom.Attributes.Color = InkColor.Opaque(255, 0, 0);
            var top = Stroke();
            top.LayerOrder = 1;
            top.InkLayerIndex = 2;
            top.Attributes.Color = InkColor.Opaque(0, 0, 255);
            using (var file = await PdfAnnotationFile.OpenAsync(original))
                await file.SaveAsync(exported, new Dictionary<int, IReadOnlyList<InkStroke>> { [0] = new[] { bottom, top } });
            using (var pdf = PdfReader.Open(exported, PdfDocumentOpenMode.Import))
            {
                var ink = Assert.Single(pdf.Pages[0].Annotations.Cast<PdfAnnotation>(),
                    annotation => annotation.Elements.GetInteger("/InkCanvasVersion") == 1);
                var appearance = ink.Elements.GetDictionary("/AP").Elements.GetDictionary("/N").Stream;
                string content = System.Text.Encoding.ASCII.GetString(appearance.UnfilteredValue);
                int red = content.IndexOf("1 0 0 rg", StringComparison.Ordinal);
                int blue = content.IndexOf("0 0 1 rg", StringComparison.Ordinal);
                Assert.True(red >= 0 && blue > red);
            }
            using var reopened = await PdfAnnotationFile.OpenAsync(exported);
            Assert.Equal(new[] { bottom.Id, top.Id }, reopened.GetInk(0).Select(stroke => stroke.Id));
            Assert.Equal(new[] { 0, 2 }, reopened.GetInk(0).Select(stroke => stroke.InkLayerIndex));
        }
        finally { Directory.Delete(root, true); }
    }

    private static InkStroke Stroke(float x = 100, bool highlighter = false, PenNib nib = PenNib.Round)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 15, Color = InkColor.Red, IsHighlighter = highlighter, HighlighterAlpha = 93, Nib = nib, TaperKind = TaperKind.Tail });
        for (int p = 0; p < 240; p++) stroke.AddPoint(x + p, 120 + MathF.Sin(p / 25f) * 40, .6f, 1000 + p * 4);
        stroke.Complete(); return stroke;
    }
    private static void CreateFixture(string path, int pages = 3, int rotation = 0, bool encrypted = false, bool repeated = false)
    {
        using var document = new PdfDocument(); document.Info.Title = "PDF annotation fixture";
        for (int i = 0; i < pages; i++)
        {
            var page = document.AddPage(); page.Width = XUnit.FromPoint(600); page.Height = XUnit.FromPoint(800); page.Rotate = rotation;
            var font = new PdfDictionary(document); font.Elements.SetName("/Type", "/Font"); font.Elements.SetName("/Subtype", "/Type1"); font.Elements.SetName("/BaseFont", "/Helvetica");
            document.Internals.AddObject(font);
            var fonts = new PdfDictionary(document); fonts.Elements["/F1"] = font.Reference;
            var resources = new PdfDictionary(document); resources.Elements["/Font"] = fonts; page.Elements["/Resources"] = resources;
            var content = new PdfDictionary(document); content.CreateStream(System.Text.Encoding.ASCII.GetBytes($"q 1 1 1 rg 0 0 600 800 re f Q BT /F1 20 Tf 40 720 Td (InkCanvas classroom page {i + 1}{(repeated ? " classroom" : "")}) Tj ET"));
            document.Internals.AddObject(content); page.Elements["/Contents"] = content.Reference;
        }
        var note = new PdfTextAnnotation { Contents = "Existing annotation", Rectangle = new PdfRectangle(new XRect(20, 20, 30, 30)) };
        document.Pages[0].Annotations.Add(note);
        document.Outlines.Add("Chapter one", document.Pages[0], true);
        if (encrypted) { document.SecuritySettings.UserPassword = "reader"; document.SecuritySettings.OwnerPassword = "owner"; }
        document.Save(path);
    }
    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public async Task SearchFindsEveryOccurrenceAndMapsHighlightsOnCroppedRotatedPages(int rotation)
    {
        string root = Path.Combine(Path.GetTempPath(), "inkcanvas-pdf-search-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "search.pdf"); CreateFixture(path, pages: 1, rotation: rotation, repeated: true);
            using (var document = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
            {
                document.Pages[0].CropBox = new PdfRectangle(new XRect(20, 30, 560, 740));
                document.Pages[0].Elements.SetReal("/UserUnit", 2);
                document.Save(path);
            }
            using var file = await PdfAnnotationFile.OpenAsync(path);
            var hits = await file.SearchAsync(" CLASSROOM ");
            Assert.Equal(2, hits.Count);
            Assert.All(hits, hit =>
            {
                Assert.Equal(0, hit.PageIndex);
                Assert.NotEmpty(hit.Bounds);
                Assert.All(hit.Bounds, bounds =>
                {
                    Assert.InRange(bounds.Left, 0, file.Pages[0].DipWidth);
                    Assert.InRange(bounds.Bottom, 0, file.Pages[0].DipHeight);
                    Assert.True(bounds.Width > 0 && bounds.Height > 0);
                });
            });
            var firstWord = Assert.Single(await file.SearchAsync("InkCanvas"));
            var firstGlyph = file.Pages[0].FromPdf(new Vector2(42, 725));
            Assert.Contains(firstWord.Bounds, bounds => bounds.Inflate(1).Contains(firstGlyph));
            Assert.Empty(await file.SearchAsync("   "));
            Assert.Empty(await file.SearchAsync("missing phrase"));
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => file.SearchAsync("classroom", cancellation.Token));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public void PageTransformRoundTripsCropOffsetsRotationAndUserUnits(int rotation)
    {
        var geometry = new PdfPageGeometry(20, -10, 620, 790, rotation, 2);
        foreach (var point in new[] { Vector2.Zero, new Vector2(100, 200), new Vector2(geometry.DipWidth, geometry.DipHeight) })
            Assert.True(Vector2.Distance(point, geometry.FromPdf(geometry.ToPdf(point))) < .001f);
    }

    [Theory]
    [InlineData(0, 1)] [InlineData(90, 1)] [InlineData(180, 2)] [InlineData(270, 2)]
    public async Task MarginInkExpandsPageAndBleedWithoutMovingContentOrAccumulatingPadding(int rotation, double unit)
    {
        string root = Path.Combine(Path.GetTempPath(), "inkcanvas-pdf-bleed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.pdf"), saved = Path.Combine(root, "saved.pdf"), again = Path.Combine(root, "again.pdf");
            CreateFixture(source, pages: 1, rotation: rotation);
            using (var document = PdfReader.Open(source, PdfDocumentOpenMode.Modify))
            {
                document.Pages[0].CropBox = new(new XRect(20, 30, 560, 740));
                document.Pages[0].Elements.SetReal("/UserUnit", unit);
                document.Save(source);
            }
            using var file = await PdfAnnotationFile.OpenAsync(source);
            var original = file.Pages[0];
            var ink = new List<InkStroke>();
            foreach (var point in new[] { new Vector2(-35, 120), new(120, -45), new(original.DipWidth + 55, 120), new(120, original.DipHeight + 65) })
            {
                var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 30, TaperKind = TaperKind.None, Nib = PenNib.Ellipse45 });
                stroke.AddPoint(point.X, point.Y); stroke.AddPoint(point.X + 8, point.Y + 4); stroke.Complete(); ink.Add(stroke);
            }
            var originalPoints = ink.Select(stroke => stroke.Points.ToArray()).ToArray();
            var expanded = PdfPageBleed.IncludeInk(original, ink);
            Assert.True(expanded.Width > original.Width && expanded.Height > original.Height);
            var pages = new Dictionary<int, IReadOnlyList<InkStroke>> { [0] = ink };
            await file.SaveAsync(saved, pages);
            await file.SaveAsync(saved, pages);
            using (var pdf = PdfReader.Open(saved, PdfDocumentOpenMode.Modify))
            {
                var page = pdf.Pages[0];
                Assert.Equal(new PdfRectangle(new XRect(20, 30, 560, 740)), page.TrimBox);
                Assert.Equal(page.CropBox, page.BleedBox);
                Assert.True(page.MediaBox.Contains(page.CropBox));
                Assert.Equal(2, page.Annotations.Count);
            }
            using var reopened = await PdfAnnotationFile.OpenAsync(saved);
            var restored = reopened.GetInk(0);
            Assert.Equal(expanded, reopened.Pages[0]);
            Assert.Equal(ink.Count, restored.Count);
            for (int stroke = 0; stroke < ink.Count; stroke++)
            {
                Assert.Equal(originalPoints[stroke], ink[stroke].Points);
                var copy = restored.Single(item => item.Id == ink[stroke].Id);
                Assert.InRange(copy.Bounds.Left, 0, expanded.DipWidth);
                Assert.InRange(copy.Bounds.Top, 0, expanded.DipHeight);
                Assert.InRange(copy.Bounds.Right, 0, expanded.DipWidth);
                Assert.InRange(copy.Bounds.Bottom, 0, expanded.DipHeight);
                for (int point = 0; point < copy.Count; point++)
                {
                    Assert.True(Vector2.Distance(original.ToPdf(ink[stroke].Points[point].Position), expanded.ToPdf(copy.Points[point].Position)) < .001f);
                    Assert.Equal(ink[stroke].Points[point].Width, copy.Points[point].Width);
                }
            }
            await reopened.SaveAsync(again, new Dictionary<int, IReadOnlyList<InkStroke>> { [0] = restored });
            using var final = await PdfAnnotationFile.OpenAsync(again);
            Assert.Equal(expanded, final.Pages[0]);
            Assert.Equal(restored[0].Points, final.GetInk(0).Single(stroke => stroke.Id == restored[0].Id).Points);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void InPageInkRetainsOriginalPageBounds()
    {
        var page = new PdfPageGeometry(20, 30, 580, 770, 0);
        Assert.Same(page, PdfPageBleed.IncludeInk(page, new[] { Stroke() }));
        Assert.Same(page, PdfPageBleed.IncludeInk(page, Array.Empty<InkStroke>()));
    }
    [Theory]
    [InlineData(false, PenNib.Round)] [InlineData(true, PenNib.Ellipse45)]
    public void EmbeddedInkRoundTripsEveryStoredCoordinateWidthAndBrush(bool highlighter, PenNib nib)
    {
        var original = Stroke(highlighter: highlighter, nib: nib);
        var loaded = Assert.Single(PdfInkCodec.Decode(PdfInkCodec.Encode(new[] { original })));
        Assert.Equal(original.Id, loaded.Id); Assert.Equal(original.Points, loaded.Points); Assert.Equal(original.Attributes.BaseWidth, loaded.Attributes.BaseWidth);
        Assert.Equal(original.Attributes.Nib, loaded.Attributes.Nib); Assert.Equal(original.Attributes.HighlighterAlpha, loaded.Attributes.HighlighterAlpha);
        Assert.Equal(StrokeGeometry.BuildOutline(original), StrokeGeometry.BuildOutline(loaded));
    }

    [Fact]
    public void BatchedInkDecodePreservesTabletSamplesAcrossBlockBoundaries()
    {
        var stroke = new InkStroke(new() { TaperKind = TaperKind.None });
        stroke.RestorePoints(Enumerable.Range(0, 513).Select(index => new InkPoint(index, 40, .6f, 2, index * 4)
        {
            HasPressure = index % 2 == 0, TiltX = index % 60, TiltY = -(index % 90),
        }));
        stroke.Complete();
        var encoded = PdfInkCodec.Encode([stroke]);
        Assert.Equal(stroke.Points, Assert.Single(PdfInkCodec.Decode(encoded)).Points);
        Assert.Throws<OperationCanceledException>(() => PdfInkCodec.Decode(encoded, new CancellationToken(canceled: true)));
    }

    [Fact]
    public async Task WatercolorStrokeAppearanceAndStyleSurvivePdfSave()
    {
        string root = Path.Combine(Path.GetTempPath(), "inkcanvas-pdf-watercolor-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.pdf"), saved = Path.Combine(root, "saved.pdf");
            CreateFixture(source);
            var attributes = new StrokeAttributes { BaseWidth = 15, Color = InkColor.Red, TaperKind = TaperKind.InkWash };
            var stroke = new InkStroke(attributes);
            for (int point = 0; point < 240; point++) stroke.AddPoint(100 + point, 120 + MathF.Sin(point / 25f) * 40, .6f, 1000 + point * 4);
            stroke.Complete();
            using (var session = await PdfAnnotationFile.OpenAsync(source))
                await session.SaveAsync(saved, new Dictionary<int, IReadOnlyList<InkStroke>> { [0] = new[] { stroke } });
            using var reopened = await PdfAnnotationFile.OpenAsync(saved);
            var restored = Assert.Single(reopened.GetInk(0));
            Assert.Equal(TaperKind.InkWash, restored.TaperKind);
            Assert.Equal(stroke.Points, restored.Points);
            Assert.Equal(InkBrush.Layers(stroke).ToArray(), InkBrush.Layers(restored).ToArray());
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task SavingPreservesOriginalPagesTextBookmarksOtherAnnotationsAndEditableInk()
    {
        string root = Path.Combine(Path.GetTempPath(), "inkcanvas-pdf-test-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string original = Path.Combine(root, "source.pdf"), saved = Path.Combine(root, "saved.pdf"); CreateFixture(original);
            var stroke = Stroke(); using (var session = await PdfAnnotationFile.OpenAsync(original))
            {
                Assert.Equal(3, session.Pages.Count);
                await session.SaveAsync(saved, new Dictionary<int, IReadOnlyList<InkStroke>> { [0] = new[] { stroke } });
            }
            using (var document = PdfReader.Open(saved, PdfDocumentOpenMode.Modify))
            { Assert.Equal(3, document.PageCount); Assert.Single(document.Outlines); Assert.Equal(2, document.Pages[0].Annotations.Count); Assert.Equal("PDF annotation fixture", document.Info.Title); }
            using var reopened = await PdfAnnotationFile.OpenAsync(saved); Assert.Equal(stroke.Points, Assert.Single(reopened.GetInk(0)).Points);
            Assert.Single(await reopened.SearchAsync("classroom page 2"));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task RepeatedSavesReplaceOwnAppearanceAndDeletingInkRemovesItFromPdf()
    {
        string root = Path.Combine(Path.GetTempPath(), "inkcanvas-pdf-repeat-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string source = Path.Combine(root, "source.pdf"), saved = Path.Combine(root, "saved.pdf"); CreateFixture(source);
            using var session = await PdfAnnotationFile.OpenAsync(source); var pages = new Dictionary<int, IReadOnlyList<InkStroke>> { [1] = new[] { Stroke() } };
            await session.SaveAsync(saved, pages); long size = new FileInfo(saved).Length;
            for (int i = 0; i < 4; i++) await session.SaveAsync(saved, pages);
            Assert.True(new FileInfo(saved).Length < size + 1024);
            using (var reopened = await PdfAnnotationFile.OpenAsync(saved)) { Assert.Single(reopened.GetInk(1)); await reopened.SaveAsync(saved, new Dictionary<int, IReadOnlyList<InkStroke>>()); }
            using var final = await PdfAnnotationFile.OpenAsync(saved); Assert.Empty(final.GetInk(1));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task InvalidPasswordAndCancelledSaveDoNotReplaceOriginalFile()
    {
        string root = Path.Combine(Path.GetTempPath(), "inkcanvas-pdf-secure-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            string path = Path.Combine(root, "source.pdf"); CreateFixture(path, encrypted: true); byte[] original = File.ReadAllBytes(path);
            await Assert.ThrowsAsync<PdfPasswordRequiredException>(() => PdfAnnotationFile.OpenAsync(path));
            using var session = await PdfAnnotationFile.OpenAsync(path, "owner"); using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SaveAsync(path, new Dictionary<int, IReadOnlyList<InkStroke>>(), cancellation.Token));
            Assert.Equal(original, File.ReadAllBytes(path));
        }
        finally { Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("1-3,2,5", 5, 4, "0,1,2,4")]
    [InlineData(" 2，4-5 ", 5, 3, "1,3,4")]
    public void ImportRangesDeduplicateValidateCapacityAndUseZeroBasedPages(string input, int count, int maximum, string expected)
        => Assert.Equal(expected, string.Join(",", PdfPageRange.Parse(input, count, maximum)));

    [Theory]
    [InlineData("0")][InlineData("4-2")][InlineData("1-999999999")][InlineData("1-2-3")][InlineData("")][InlineData("1,2,3,4")]
    public void InvalidImportRangesDoNotExpandOrPartiallyImport(string input)
        => Assert.Throws<FormatException>(() => PdfPageRange.Parse(input, 10, 3));

    [Fact]
    public async Task ExternalFileChangesCannotBeOverwrittenByStaleWorkspace()
    {
        string path = Path.Combine(Path.GetTempPath(), "inkcanvas-pdf-conflict-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            CreateFixture(path);
            using var file = await PdfAnnotationFile.OpenAsync(path);
            File.AppendAllText(path, "\n% Changed by another editor\n");
            byte[] updated = File.ReadAllBytes(path);
            await Assert.ThrowsAsync<IOException>(() => file.SaveAsync(path, new Dictionary<int, IReadOnlyList<InkStroke>>()));
            Assert.Equal(updated, File.ReadAllBytes(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task UserPasswordRespectsReadOnlyPermissionAndOwnerCanAnnotate()
    {
        string path = Path.Combine(Path.GetTempPath(), "inkcanvas-pdf-permissions-" + Guid.NewGuid().ToString("N") + ".pdf");
        try
        {
            using (var pdf = new PdfDocument())
            {
                pdf.AddPage(); pdf.SecuritySettings.UserPassword = "reader"; pdf.SecuritySettings.OwnerPassword = "owner";
                pdf.SecuritySettings.PermitModifyDocument = false; pdf.SecuritySettings.PermitAnnotations = false; pdf.Save(path);
            }
            using var reader = await PdfAnnotationFile.OpenAsync(path, "reader"); Assert.True(reader.ReadOnly);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => reader.SaveAsync(path, new Dictionary<int, IReadOnlyList<InkStroke>>()));
            using var owner = await PdfAnnotationFile.OpenAsync(path, "owner"); Assert.False(owner.ReadOnly);
        }
        finally { File.Delete(path); }
    }
}
