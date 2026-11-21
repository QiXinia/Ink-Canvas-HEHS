using InkCanvas.Core.Ink;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.Geometry;
using System.Numerics;

namespace InkCanvas.App.Diagnostics;

internal static class RenderingRegression
{
    public static bool Run(List<object> checks)
    {
        var device = CanvasDevice.GetSharedDevice();
        using var target = new CanvasRenderTarget(device, 128, 128, 96);
        using var reference = new CanvasRenderTarget(device, 128, 128, 96);
        using var renderer = new StrokeRenderer();
        bool allPassed = true;
        foreach (var nib in new[] { PenNib.Round, PenNib.Ellipse45 })
        foreach (var points in new[]
        {
            new[] { new Vector2(50, 50) },
            new[] { new Vector2(20, 20), new Vector2(100, 100), new Vector2(20, 100), new Vector2(100, 20) },
            new[] { new Vector2(20, 50), new Vector2(60, 50), new Vector2(60, 50), new Vector2(100, 60) },
        })
        {
            var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 14, IsHighlighter = true, Nib = nib });
            foreach (var p in points) stroke.AddPoint(p.X, p.Y);
            stroke.Complete();
            using (var draw = target.CreateDrawingSession())
            {
                draw.Clear(Microsoft.UI.Colors.Transparent);
                renderer.Draw(draw, stroke, Matrix3x2.Identity);
            }
            using (var draw = reference.CreateDrawingSession())
            {
                draw.Clear(Microsoft.UI.Colors.Transparent);
                var regions = new List<CanvasGeometry>();
                try
                {
                    foreach (var contour in StrokeGeometry.BuildUnionContours(stroke))
                    {
                        using var builder = new CanvasPathBuilder(draw);
                        builder.BeginFigure(contour[0]);
                        for (int i = 1; i < contour.Length; i++) builder.AddLine(contour[i]);
                        builder.EndFigure(CanvasFigureLoop.Closed);
                        regions.Add(CanvasGeometry.CreatePath(builder));
                    }
                    using var group = CanvasGeometry.CreateGroup(draw, regions.ToArray(), CanvasFilledRegionDetermination.Winding);
                    var c = stroke.Attributes.EffectiveColor;
                    draw.FillGeometry(group, Windows.UI.Color.FromArgb(c.A, c.R, c.G, c.B));
                }
                finally { foreach (var region in regions) region.Dispose(); }
            }
            var actual = target.GetPixelBytes();
            var expected = reference.GetPixelBytes();
            bool identical = actual.Zip(expected).All(pair => Math.Abs(pair.First - pair.Second) <= 1);
            allPassed &= identical;
            checks.Add(new { check = "compact stroke geometry matches original raster", points = points.Length, nib = nib.ToString(), identical });
            renderer.InvalidateAll();
        }
        var first = Dot(20, 20);
        var second = Dot(40, 40);
        using (var draw = target.CreateDrawingSession())
        {
            renderer.Draw(draw, first, Matrix3x2.Identity);
            long builds = renderer.GeometryBuildCount;
            renderer.Retain(new[] { first, second });
            renderer.Draw(draw, first, Matrix3x2.Identity);
            bool reused = builds == renderer.GeometryBuildCount;
            first.Transform(Matrix3x2.CreateTranslation(10, 0));
            renderer.Retain(new[] { first });
            renderer.Draw(draw, first, Matrix3x2.Identity);
            bool rebuilt = renderer.GeometryBuildCount == builds + 1;
            renderer.Retain(Array.Empty<InkStroke>());
            bool evicted = renderer.CachedGeometryCount == 0;
            allPassed &= reused && rebuilt && evicted;
            checks.Add(new { check = "cache retains unchanged ink and evicts edited/deleted ink", reused, rebuilt, evicted });
            for (int i = 0; i < 2060; i++) renderer.Draw(draw, Dot(20, 20), Matrix3x2.Identity);
            bool bounded = renderer.CachedGeometryCount <= 2048;
            allPassed &= bounded;
            checks.Add(new { check = "native geometry cache has a fixed entry bound", bounded });
        }
        return allPassed;

        static InkStroke Dot(float x, float y)
        {
            var stroke = new InkStroke(new StrokeAttributes());
            stroke.AddPoint(x, y);
            stroke.Complete();
            return stroke;
        }
    }
}
