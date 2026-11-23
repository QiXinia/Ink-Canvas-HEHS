using System.Text.Json;
using System.Runtime.InteropServices;
using InkCanvas.App.Services;
using InkCanvas.Ink;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class PenOptionsRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var services = new AppServices(Path.Combine(directory, Guid.NewGuid().ToString("N")));
        services.Settings.Startup.IsAutoUpdate = false;
        using var shell = new ShellController(services);
        shell.Start();
        shell.StartInClickThrough();
        var checks = new List<object>();
        try
        {
            foreach (bool highlighter in new[] { false, true })
            {
                shell.OpenPenPalette(highlighter);
                var window = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
                await Task.Delay(250);
                var palette = Descendants(window.Content).OfType<PenPalette>().Single();
                Check("pen options stay nonactivating " + highlighter,
                    NativeMethods.HasExStyle(WindowChrome.GetHwnd(window), NativeMethods.WS_EX_NOACTIVATE));
                var nib = Descendants(palette).OfType<SegmentedSelector>().Single(selector => AutomationProperties.GetName(selector) == "笔尖");
                var ellipse = Descendants(nib).OfType<ToggleButton>().Last();
                bool? clicked = await TryPointer(window, ellipse, drag: false);
                checks.Add(new { check = "native click changes nib while palette is inactive " + highlighter, available = clicked.HasValue,
                    passed = clicked.HasValue ? (bool?)(clicked.Value && services.OverlayInk.CurrentAttributes.Nib == PenNib.Ellipse45) : null });
                foreach (int index in new[] { 1, 0 })
                {
                    Toggle(Descendants(nib).OfType<ToggleButton>().ElementAt(index));
                    Check("nib buttons update and save " + highlighter + " " + index,
                        (int)services.OverlayInk.CurrentAttributes.Nib == index && (int)services.Store.Load().Canvas.PenNib == index);
                }
                if (!highlighter)
                {
                    var stylePicker = Descendants(palette).OfType<PenStylePicker>().Single();
                    var styleButtons = Descendants(stylePicker).OfType<ToggleButton>().ToArray();
                    Check("pen styles use compact actual-stroke icons", styleButtons.Length == OriginalMenuCatalog.PenStyles.Count
                        && styleButtons.All(button => button.Content is Grid content
                            && Descendants(content).OfType<PenStyleIcon>().Count() == 1
                            && Descendants(content).OfType<TextBlock>().Any()));
                    var style = stylePicker;
                    checks.Add(new { check = "pen style choices use compact two-row icons", height = style.ActualHeight,
                        passed = style.ActualHeight <= 72 && Descendants(style).OfType<ToggleButton>().Count() == OriginalMenuCatalog.PenStyles.Count });
                    checks.Add(new { check = "one shared preview keeps common settings accessible",
                        previewCount = Descendants(palette).OfType<PenStrokePreview>().Count(), cardHeight = palette.CardSurface.ActualHeight,
                        passed = Descendants(palette).OfType<PenStrokePreview>().Count() == 1 && palette.CardSurface.ActualHeight <= 460 });
                    foreach (int index in Enumerable.Range(0, OriginalMenuCatalog.PenStyles.Count).Reverse())
                    {
                        Toggle(Descendants(style).OfType<ToggleButton>().ElementAt(index));
                        Check("style buttons update and save " + index, services.Store.Load().Canvas.InkStyle == index
                            && services.OverlayInk.CurrentAttributes.TaperKind == InkCanvas.Core.Settings.OriginalMenuCatalog.ToTaperKind(index));
                    }
                }
                var width = Descendants(palette).OfType<Slider>().Single(slider => AutomationProperties.GetName(slider).StartsWith(highlighter ? "荧光笔粗细" : "画笔粗细"));
                var widthPeer = new SliderAutomationPeer(width);
                ((IRangeValueProvider)widthPeer.GetPattern(PatternInterface.RangeValue)).SetValue(18);
                await Task.Delay(260);
                Check("width updates and saves " + highlighter, services.OverlayInk.CurrentAttributes.BaseWidth == 9
                    && (highlighter ? services.Store.Load().Canvas.HighlighterWidth ?? services.Store.Load().Canvas.InkWidth : services.Store.Load().Canvas.InkWidth) == 9);
                bool? dragged = await TryPointer(window, width, drag: true);
                checks.Add(new { check = "native drag changes width while palette is inactive " + highlighter, available = dragged.HasValue,
                    value = width.Value, storedWidth = highlighter ? services.Store.Load().Canvas.HighlighterWidth : services.Store.Load().Canvas.InkWidth, liveWidth = services.OverlayInk.CurrentAttributes.BaseWidth,
                    passed = dragged.HasValue ? (bool?)(dragged.Value && services.OverlayInk.CurrentAttributes.BaseWidth > 10
                        && (highlighter ? services.Store.Load().Canvas.HighlighterWidth : services.Store.Load().Canvas.InkWidth) == services.OverlayInk.CurrentAttributes.BaseWidth) : null });
                ((IRangeValueProvider)widthPeer.GetPattern(PatternInterface.RangeValue)).SetValue(18);
                if (highlighter)
                {
                    width.Value = 120;
                    await Task.Delay(260);
                    Check("highlighter supports full 60 DIP width", services.OverlayInk.CurrentAttributes.BaseWidth == 60
                        && services.Store.Load().Canvas.HighlighterWidth == 60);
                    services.OverlayInk.Tool = InkTool.Pen;
                    Check("pen restores its independent width", services.OverlayInk.CurrentAttributes.BaseWidth == services.Settings.Canvas.InkWidth);
                    services.OverlayInk.Tool = InkTool.Highlighter;
                    Check("highlighter restores its independent width", services.OverlayInk.CurrentAttributes.BaseWidth == 60);
                    width.Value = 18;
                    var alpha = Descendants(palette).OfType<Slider>().Single(slider => AutomationProperties.GetName(slider).StartsWith("荧光笔不透明度"));
                    ((IRangeValueProvider)new SliderAutomationPeer(alpha).GetPattern(PatternInterface.RangeValue)).SetValue(80);
                    await Task.Delay(260);
                    Check("alpha updates and saves", services.OverlayInk.CurrentAttributes.HighlighterAlpha == 204 && services.Store.Load().Canvas.InkAlpha == 204);
                }
                Check("inline choices fit palette " + highlighter, Descendants(palette).OfType<SegmentedSelector>().All(selector => Fits(selector, palette.CardSurface)));
                await BoardWindowRegression.SaveVisual(palette, Path.Combine(directory, highlighter ? "highlighter.png" : "pen.png"));
                window.Close();
                shell.OpenPenPalette(highlighter);
                await Task.Delay(100);
                window = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
                Check("reopening keeps pen width " + highlighter, Descendants(window.Content).OfType<Slider>()
                    .Single(slider => AutomationProperties.GetName(slider).StartsWith(highlighter ? "荧光笔粗细" : "画笔粗细")).Value == 18);
                window.Close();
            }

            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            foreach (double scale in new[] { 50d, 100d, 150d, 200d })
            {
                services.Settings.Appearance.IsEnableDisPlayFloatBarText = true;
                bar.Bar.RefreshSettingsVisuals();
                bar.Reposition(100, scale);
                await Task.Delay(150);
                Check("toolbar glyphs fit their buttons " + scale, IconsFit(bar.Bar));
            }
            bar.Reposition(100, 100);
            var moreButton = Descendants(bar.Bar).OfType<TouchIconButton>().Single(button => button.Label == "工具");
            ((IInvokeProvider)new ButtonAutomationPeer(moreButton).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(200);
            var more = bar.GetOpenPanel(BarPanelKind.More);
            Check("more menu glyphs fit their buttons", more is not null && IconsFit(more.Content));
            if (more is not null) await BoardWindowRegression.SaveVisual((FrameworkElement)more.Content, Path.Combine(directory, "more.png"));
            bar.ClosePanels();
            await CheckTapRendering(shell, checks, directory);
            shell.ToggleBoard();
            await Task.Delay(350);
            var board = shell.OpenWindows.OfType<BoardWindow>().Single();
            Check("board glyphs fit their buttons", IconsFit(board.Content));
            board.ShowPenPalette();
            await Task.Delay(150);
            var boardPalette = board.ToolPanels.SelectMany(panel => Descendants(panel.Content)).OfType<BoardPenPalette>().Single();
            var boardStyles = Descendants(boardPalette).OfType<PenStylePicker>().Single();
            foreach (int index in Enumerable.Range(0, OriginalMenuCatalog.PenStyles.Count))
            {
                Toggle(Descendants(boardStyles).OfType<ToggleButton>().ElementAt(index));
                Check("board style applies and saves " + index,
                    board.CurrentController.CurrentAttributes.TaperKind == OriginalMenuCatalog.ToTaperKind(index)
                    && services.Store.Load().Canvas.InkStyle == index);
            }
            Check("board style samples fit palette", Fits(boardStyles, boardPalette));
            checks.Add(new { check = "board pen style choices use the compact layout", height = boardStyles.ActualHeight,
                passed = boardStyles.ActualHeight <= 72 && Descendants(boardPalette).OfType<PenStrokePreview>().Count() == 2 });
            board.CurrentController.CurrentAttributes.BaseWidth = 12;
            board.CurrentController.CurrentAttributes.TaperKind = TaperKind.Calligraphy;
            var boardTap = new InkCanvas.Platform.Windows.OverlayPointer(9100, 360, 240, .5f, 0, 0,
                InkCanvas.Platform.Windows.OverlayPointerKind.Pen, 0);
            board.CurrentController.OnPointerDown(boardTap);
            board.CurrentController.OnPointerUp(boardTap with { TimeMs = 15 });
            await Task.Delay(350);
            var boardTapStroke = board.CurrentController.Document.Strokes.Last();
            checks.Add(new { check = "board tap reaches configured diameter through its animation clock",
                activeDotAnimations = board.CurrentController.DotAnimations.Count, width = boardTapStroke.Points[0].Width,
                passed = board.CurrentController.DotAnimations.Count == 0 && boardTapStroke.Points[0].Width == 12 });
            await BoardWindowRegression.SaveVisual(boardPalette, Path.Combine(directory, "board-styles.png"));
            await BoardWindowRegression.SaveVisual((FrameworkElement)board.Content, Path.Combine(directory, "board.png"));
            board.Close();

            foreach (var kind in new[] { TaperKind.SmoothPen, TaperKind.SoftPen, TaperKind.Natural, TaperKind.Calligraphy, TaperKind.InkWash })
            {
                using var scene = new InkSceneRenderer();
                using var reference = new InkSceneRenderer();
                using var live = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 640, 240, 96);
                using var dry = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 640, 240, 96);
                var stroke = new InkStroke(new StrokeAttributes { TaperKind = kind, BaseWidth = 9 });
                for (int index = 0; index < 220; index++)
                {
                    stroke.AddPoint(20 + index * 2.5f, 100 + MathF.Sin(index * .08f) * 25, .5f, index * 8);
                    using var drawing = live.CreateDrawingSession();
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    scene.Strokes.Draw(drawing, stroke, System.Numerics.Matrix3x2.Identity);
                }
                stroke.Complete();
                using (var drawing = dry.CreateDrawingSession())
                {
                    drawing.Clear(Microsoft.UI.Colors.Transparent);
                    reference.Strokes.Draw(drawing, stroke, System.Numerics.Matrix3x2.Identity);
                }
                Check("cached live rendering matches completed style " + kind,
                    live.GetPixelBytes().Zip(dry.GetPixelBytes()).All(pair => Math.Abs(pair.First - pair.Second) <= 1));
                await dry.SaveAsync(Path.Combine(directory, "stroke-" + kind + ".png"), CanvasBitmapFileFormat.Png);
            }
        }
        catch (Exception exception) { checks.Add(new { check = "pen options regression exception", passed = false, error = exception.ToString() }); }
        finally
        {
            shell.OpenWindows.OfType<BoardWindow>().FirstOrDefault()?.Close();
            foreach (var window in shell.OpenWindows.ToArray()) window.Close();
            File.WriteAllText(Path.Combine(directory, "pen-options.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }

        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }

    private static void Toggle(ToggleButton button) => ((IToggleProvider)new ToggleButtonAutomationPeer(button).GetPattern(PatternInterface.Toggle)).Toggle();

    private static async Task CheckTapRendering(ShellController shell, List<object> checks, string directory)
    {
        var controller = shell.Services.OverlayInk;
        controller.Tool = InkCanvas.Ink.InkTool.Pen;
        controller.CurrentAttributes.TaperKind = TaperKind.Calligraphy;
        controller.CurrentAttributes.BaseWidth = 12;
        var sample = new InkCanvas.Platform.Windows.OverlayPointer(9200, 500, 300, .5f, 0, 0,
            InkCanvas.Platform.Windows.OverlayPointerKind.Pen, 0);
        if (shell.Overlay.ClickThrough) shell.ToggleClickThrough();
        controller.OnPointerDown(sample);
        long bytesBefore = shell.Overlay.Presenter.GpuReadbackBytes;
        controller.OnPointerUp(sample with { TimeMs = 15 });
        bool initialSmall = Alpha(504, 300) < 30;
        await Task.Delay(200);
        bool finalFull = Alpha(504, 300) > 200;
        checks.Add(new { check = "desktop tap grows to full diameter and settles", passed = initialSmall && finalFull && controller.DotAnimations.Count == 0 });
        long frameBytes = (long)shell.Overlay.Window.Width * shell.Overlay.Window.Height * 4;
        long readback = shell.Overlay.Presenter.GpuReadbackBytes - bytesBefore;
        checks.Add(new { check = "tap animation reads small live regions between commits", passed = readback < frameBytes * 3 + 512 * 1024, readback });

        using var scene = new InkSceneRenderer();
        using var tiles = new CommittedInkLayer();
        using var cached = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 240, 160, 96);
        using var direct = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 240, 160, 96);
        using var ink = new InkCanvas.Ink.InkController { RecognizeShapes = false };
        ink.CurrentAttributes.BaseWidth = 24;
        ink.CurrentAttributes.TaperKind = TaperKind.Natural;
        var point = sample with { X = 100, Y = 80 };
        ink.OnPointerDown(point);
        ink.OnPointerUp(point);
        foreach (int elapsed in new[] { 0, 30, 60, 120 })
        {
            if (elapsed > 0) ink.AdvanceDotAnimations(elapsed == 120 ? 60 : 30);
            using (var draw = direct.CreateDrawingSession()) { draw.Clear(Microsoft.UI.Colors.Transparent); scene.Draw(draw, ink); }
            using (var draw = cached.CreateDrawingSession())
            {
                draw.Clear(Microsoft.UI.Colors.Transparent);
                tiles.Draw(draw, scene, ink, 240, 160);
                scene.DrawLive(draw, ink);
            }
            bool identical = cached.GetPixelBytes().Zip(direct.GetPixelBytes()).All(pair => Math.Abs(pair.First - pair.Second) <= 1);
            checks.Add(new { check = "cached and direct tap rendering match " + elapsed, passed = identical });
            await direct.SaveAsync(Path.Combine(directory, "tap-" + elapsed + ".png"), CanvasBitmapFileFormat.Png);
        }

        ink.Document.Clear();
        var jitterStart = point with { Id = 9201, X = 102.8f, Kind = InkCanvas.Platform.Windows.OverlayPointerKind.Touch };
        ink.OnPointerDown(jitterStart);
        for (uint index = 1; index <= 24; index++)
            ink.OnPointerMove(jitterStart with { X = 100 + MathF.Cos(index * 2.3f) * 2.8f,
                Y = 80 + MathF.Sin(index * 2.3f) * 2.8f, TimeMs = index * 8 });
        ink.OnPointerUp(jitterStart with { X = 97.4f, TimeMs = 200 });
        checks.Add(new { check = "infrared jitter confirms a full width point", passed = ink.Document.Count == 1
            && ink.Document.Strokes[0].Count == 1 && ink.Document.Strokes[0].Points[0].Width == 24 });
        foreach (int elapsed in new[] { 0, 30, 60, 120 })
        {
            if (elapsed > 0) ink.AdvanceDotAnimations(elapsed == 120 ? 60 : 30);
            using (var draw = direct.CreateDrawingSession()) { draw.Clear(Microsoft.UI.Colors.Transparent); scene.Draw(draw, ink); }
            using (var draw = cached.CreateDrawingSession())
            {
                draw.Clear(Microsoft.UI.Colors.Transparent);
                tiles.Draw(draw, scene, ink, 240, 160);
                scene.DrawLive(draw, ink);
            }
            bool identical = cached.GetPixelBytes().Zip(direct.GetPixelBytes()).All(pair => Math.Abs(pair.First - pair.Second) <= 1);
            checks.Add(new { check = "cached jitter point morph matches direct rendering " + elapsed, passed = identical });
            await direct.SaveAsync(Path.Combine(directory, "jitter-tap-" + elapsed + ".png"), CanvasBitmapFileFormat.Png);
        }

        byte Alpha(float x, float y)
        {
            var window = shell.Overlay.Window;
            int px = (int)MathF.Round(x * window.DpiScale), py = (int)MathF.Round(y * window.DpiScale);
            return Marshal.ReadByte(window.PixelBuffer, (py * window.Width + px) * 4 + 3);
        }
    }

    internal static async Task<bool?> TryPointer(Window window, FrameworkElement control, bool drag)
    {
        if (!NativeMethods.GetCursorPos(out var original)) return null;
        var bounds = WindowChrome.GetScreenBounds(window, control);
        int startX = bounds.X + (int)(bounds.Width * (drag ? .2 : .5));
        int y = bounds.Y + bounds.Height / 2;
        bool down = false;
        try
        {
            if (!SetCursorPos(startX, y)) return null;
            var point = new NativeMethods.POINT { X = startX, Y = y };
            if (GetAncestor(WindowFromPoint(point), 2) != WindowChrome.GetHwnd(window)) return null;
            MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
            down = true;
            await Task.Delay(40);
            if (drag)
            {
                for (int step = 1; step <= 10; step++)
                {
                    SetCursorPos(startX + bounds.Width * step * 6 / 100, y);
                    await Task.Delay(15);
                }
            }
            MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
            down = false;
            await Task.Delay(100);
            return true;
        }
        finally
        {
            if (down) MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
            SetCursorPos(original.X, original.Y);
        }
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);

    private static bool IconsFit(DependencyObject root)
    {
        foreach (var button in Descendants(root).OfType<Button>())
        {
            if (button.Visibility != Visibility.Visible || button.ActualWidth <= 0 || button.ActualHeight <= 0) continue;
            foreach (var icon in Descendants(button).OfType<FontIcon>())
                if (icon.Visibility == Visibility.Visible && icon.ActualWidth > 0 && !Fits(icon, button)) return false;
        }
        return true;
    }

    private static bool Fits(FrameworkElement element, FrameworkElement container)
    {
        var bounds = element.TransformToVisual(container).TransformBounds(new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        return bounds.Left >= -.5 && bounds.Top >= -.5 && bounds.Right <= container.ActualWidth + .5 && bounds.Bottom <= container.ActualHeight + .5;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
