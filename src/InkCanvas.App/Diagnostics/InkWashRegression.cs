using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class InkWashRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        var window = new SettingsWindow(services);
        window.Activate();
        try
        {
            window.SelectCategory("画板选项");
            await Task.Delay(250);
            var sliders = Descendants(window.Content).OfType<Slider>().Where(slider => AutomationProperties.GetName(slider).StartsWith("水墨")).ToArray();
            Check("four water-ink editors expose requested defaults", sliders.Length == 4
                && sliders.Select(slider => slider.Value).SequenceEqual(new double[] { 30, 42, 44, 44 }));
            double[] changed = { 38, 65, 72, 60 };
            for (int i = 0; i < sliders.Length; i++) sliders[i].Value = changed[i];
            var saved = services.Store.Load().Canvas;
            Check("water-ink edits save and reach the drawing controller", saved.InkWashWidth == 38
                && saved.InkWash == new InkWashBrushOptions(.65f, 72, .60f)
                && services.OverlayInk.InkWashWidth == 38 && services.OverlayInk.CurrentAttributes.InkWash == saved.InkWash);
            var hits = window.SearchSettings("水墨");
            Check("water-ink options are searchable", sliders.All(slider => hits.Any(hit => hit.Title == AutomationProperties.GetName(slider))));
            window.NavigateToSetting(hits.First(hit => hit.Title == "水墨笔锋粗细（px）"));
            await Task.Delay(150);
            await BoardWindowRegression.SaveVisual((FrameworkElement)window.Content, Path.Combine(directory, "settings.png"));

            var palette = new PenPalette { Controller = services.OverlayInk };
            var boardPalette = new BoardPenPalette(services.Board.Current, services.Settings, 1);
            Check("toolbar palettes contain no water-ink parameter editors", new DependencyObject[] { palette, boardPalette }
                .All(root => !Descendants(root).OfType<Slider>().Any(slider => AutomationProperties.GetName(slider).StartsWith("水墨"))));

            using var liveTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 960, 360, 96);
            using var coldTarget = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 960, 360, 96);
            foreach (bool hardware in new[] { false, true })
            foreach (float width in new[] { 3f, 30, 90 })
            {
                var stroke = new InkStroke(new() { BaseWidth = width, TaperKind = TaperKind.InkWash, InkWashSeed = 137 });
                using var live = new StrokeRenderer();
                for (int i = 0; i < 240; i++)
                {
                    stroke.AddPoint(80 + i * 3, 170 + MathF.Sin(i * .055f) * 50, .35f + .3f * MathF.Sin(i * .03f), i * 4,
                        hardware, hardware ? 25 : 0, hardware ? -35 : 0);
                    using var drawing = liveTarget.CreateDrawingSession();
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    live.Draw(drawing, stroke, Matrix3x2.Identity);
                }
                var before = liveTarget.GetPixelBytes();
                stroke.Complete();
                Draw(live, stroke, liveTarget);
                Check($"completion preserves every live pixel {hardware} {width}", before.SequenceEqual(liveTarget.GetPixelBytes()));
                using var cold = new StrokeRenderer();
                Draw(cold, stroke, coldTarget);
                var after = coldTarget.GetPixelBytes();
                Check($"water-ink renders visible pixels {hardware} {width}", Enumerable.Range(0, after.Length / 4).Any(p => after[p * 4 + 3] > 0));
                int maxError = before.Zip(after).Max(pair => Math.Abs(pair.First - pair.Second));
                Check($"cold geometry matches frozen chunks {hardware} {width}", maxError <= 1, new { maxError });
                long builds = live.GeometryBuildCount;
                for (int frame = 0; frame < 10; frame++)
                {
                    using var drawing = liveTarget.CreateDrawingSession();
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    live.Draw(drawing, stroke, Matrix3x2.CreateTranslation(frame, 0));
                }
                Check($"panning reuses cached geometry {hardware} {width}", live.GeometryBuildCount == builds);
                stroke.Attributes.InkWash = new(.9f, 80, .8f);
                Draw(live, stroke, liveTarget);
                Check($"brush parameter changes invalidate cached geometry {hardware} {width}", live.GeometryBuildCount > builds);
                live.Retain(Array.Empty<InkStroke>());
                Check($"removed ink releases native geometry {hardware} {width}", live.CachedGeometryCount == 0);
                if (!hardware && width == 30) await coldTarget.SaveAsync(Path.Combine(directory, "water-ink.png"), CanvasBitmapFileFormat.Png);
            }

            using (var renderer = new StrokeRenderer())
            {
                var stroke = new InkStroke(new() { BaseWidth = 30, TaperKind = TaperKind.InkWash, InkWashSeed = 137 });
                var times = new List<double>();
                for (int i = 0; i < 2000; i++)
                {
                    stroke.AddPoint(60 + i * 2.5f, 160 + MathF.Sin(i * .025f) * 50, .5f, i * 4);
                    if (i < 2 || i % 8 != 0) continue;
                    var timer = Stopwatch.StartNew();
                    Draw(renderer, stroke, liveTarget);
                    times.Add(timer.Elapsed.TotalMilliseconds);
                }
                var ordered = times.Skip(4).Order().ToArray();
                Check("long strokes rebuild a bounded moving tail", renderer.SegmentBuildCount < stroke.Count * 20,
                    new { points = stroke.Count, renderer.SegmentBuildCount, frames = times.Count,
                        medianMilliseconds = ordered[ordered.Length / 2], p95Milliseconds = ordered[(int)(ordered.Length * .95)] });
                Draw(renderer, stroke, liveTarget);
                long builds = renderer.GeometryBuildCount;
                Draw(renderer, stroke, liveTarget);
                Check("unchanged live frame reuses native geometry", builds == renderer.GeometryBuildCount);
            }
        }
        catch (Exception ex) { checks.Add(new { check = "water-ink regression exception", passed = false, error = ex.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "ink-wash.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            window.Close();
            Application.Current.Exit();
        }
        void Check(string check, bool passed, object detail = null) => checks.Add(new { check, passed, detail });
    }

    private static void Draw(StrokeRenderer renderer, InkStroke stroke, CanvasRenderTarget target)
    {
        using var drawing = target.CreateDrawingSession();
        drawing.Clear(Microsoft.UI.Colors.Transparent);
        renderer.Draw(drawing, stroke, Matrix3x2.Identity);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, i))) yield return child;
    }
}
