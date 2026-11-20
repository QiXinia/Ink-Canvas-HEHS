using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Replay;
using InkCanvas.Core.Shapes;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Media;
using System.Numerics;
using System.Text.Json;
using System.Runtime.InteropServices;

namespace InkCanvas.App.Diagnostics;

/// <summary>
/// Opt-in runtime check (<c>--selftest</c>). Draws through the real input path and verifies,
/// in the running app, that committing a stroke does not change its geometry — the property
/// whose absence caused the original stroke-taper flicker.
/// </summary>
public static class SelfTest
{
    public static async Task Run(ShellController shell, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var controller = shell.Services.OverlayInk;
        var checks = new List<object>();
        bool renderingMatches = RenderingRegression.Run(checks);
        bool erasingWorks = EraserRegression.Run(checks);
        bool lassoErasingWorks = LassoEraserRegression.Run(checks);
        bool boardFeaturesWork = BoardFeatureRegression.Run(checks);
        int playbackCheckStart = checks.Count;
        bool playbackWorks = PlaybackRegression.Run(checks) && PlaybackRegression.CheckCachedRendering(checks);
        File.WriteAllText(Path.Combine(outputDirectory, "playback.json"), JsonSerializer.Serialize(
            new { passed = playbackWorks, checks = checks.Skip(playbackCheckStart).ToArray() },
            new JsonSerializerOptions { WriteIndented = true }));
        await LassoEraserRegression.SavePreview(outputDirectory);
        bool selectionWorks = SelectionRegression.Run(checks);
        bool? boardSelectionWorks = await SelectionWindowRegression.Run(checks, outputDirectory);
        await SelectionRegression.SavePreview(outputDirectory);
        bool lassoButtonWorks = await CheckLassoButton(shell, checks, outputDirectory);
        bool chromeFits = await CheckFloatingBarLayout(shell, checks);
        bool positionStable = await CheckToolbarPosition(shell, checks);
        bool? translucent = await CheckBackdropTransparency(shell, checks);
        bool palettesFit = await CheckPaletteLayout(shell, checks, outputDirectory);
        bool panelsFit = await CheckPopupLayout(shell, checks);
        bool tapsWork = CheckTapInk(checks);
        bool unfoldingWorks = await CheckUnfoldHandles(shell, checks);
        bool popupOrder = await CheckPopupOrder(shell, checks);

        // 1. Freehand tapered stroke, fed one sample at a time exactly like a digitizer.
        controller.CurrentAttributes.Color = InkColor.Opaque(220, 38, 38);
        controller.CurrentAttributes.BaseWidth = 14f;
        controller.CurrentAttributes.TaperKind = TaperKind.Tail;
        controller.Tool = InkTool.Pen;

        const uint penId = 9001;
        var path = new List<Vector2>();
        for (int i = 0; i < 70; i++)
            path.Add(new Vector2(220 + i * 9f, 300 + MathF.Sin(i * 0.16f) * 70f));

        controller.OnPointerDown(Sample(penId, path[0], 0));
        long readbackBeforeDrawing = shell.Overlay.Presenter.GpuReadbackBytes;
        long framesBeforeDrawing = shell.Overlay.Presenter.PresentedFrames;
        for (int i = 1; i < path.Count; i++)
            controller.OnPointerMove(Sample(penId, path[i], (uint)(i * 8)));
        long drawingReadback = shell.Overlay.Presenter.GpuReadbackBytes - readbackBeforeDrawing;
        long drawingFrames = Math.Max(1, shell.Overlay.Presenter.PresentedFrames - framesBeforeDrawing);

        var live = controller.LiveStrokes.First();
        float[] widthsBeforeCommit = live.Points.Select(p => p.Width).ToArray();
        Vector2[] outlineBeforeCommit = StrokeGeometry.BuildOutline(live);
        bool tipTaperedLive = live.Points[^1].Width < live.Points[10].Width;

        controller.OnPointerUp(Sample(penId, path[^1], (uint)(path.Count * 8)));

        var committed = controller.Document.Strokes[^1];
        float[] widthsAfterCommit = committed.Points.Select(p => p.Width).ToArray();
        Vector2[] outlineAfterCommit = StrokeGeometry.BuildOutline(committed);

        checks.Add(new
        {
            check = "committed geometry equals last live frame",
            widthsIdentical = widthsBeforeCommit.SequenceEqual(widthsAfterCommit),
            outlineIdentical = outlineBeforeCommit.SequenceEqual(outlineAfterCommit),
            taperPresentWhileDrawing = tipTaperedLive,
            tipWidth = widthsAfterCommit[^1],
            settledWidth = widthsAfterCommit[10],
        });

        // 2. Highlighter (must never taper).
        controller.Tool = InkTool.Highlighter;
        controller.CurrentAttributes.Color = InkColor.Opaque(234, 179, 8);
        controller.CurrentAttributes.BaseWidth = 34f;
        const uint hiId = 9002;
        controller.OnPointerDown(Sample(hiId, new Vector2(220, 460), 0));
        for (int i = 1; i < 50; i++)
            controller.OnPointerMove(Sample(hiId, new Vector2(220 + i * 12f, 460), (uint)(i * 8)));
        controller.OnPointerUp(Sample(hiId, new Vector2(220 + 49 * 12f, 460), 400));
        var highlighter = controller.Document.Strokes[^1];
        checks.Add(new
        {
            check = "highlighter is uniform width",
            uniform = highlighter.Points.All(p => Math.Abs(p.Width - 34f) < 0.01f),
        });

        // 3. Preset shapes through the shape tool.
        controller.Tool = InkTool.Shape;
        controller.CurrentAttributes.Color = InkColor.Opaque(37, 99, 235);
        controller.CurrentAttributes.BaseWidth = 4f;
        controller.RecognizeShapes = false; // keep the synthetic shapes verbatim
        int before = controller.Document.Count;
        controller.ShapeKind = ShapeKind.Coordinate3D;
        const uint shapeId = 9003;
        controller.OnPointerDown(Sample(shapeId, new Vector2(1150, 620), 0));
        controller.OnPointerMove(Sample(shapeId, new Vector2(1420, 400), 40));
        controller.OnPointerUp(Sample(shapeId, new Vector2(1420, 400), 80));
        checks.Add(new
        {
            check = "3D coordinate system committed as three axes",
            strokesAdded = controller.Document.Count - before,
        });

        controller.Tool = InkTool.Shape;
        controller.ShapeKind = ShapeKind.Circle;
        controller.OnPointerDown(Sample(shapeId, new Vector2(300, 700), 0));
        controller.OnPointerMove(Sample(shapeId, new Vector2(520, 900), 40));
        controller.OnPointerUp(Sample(shapeId, new Vector2(520, 900), 80));

        controller.Tool = InkTool.Pen;
        controller.RecognizeShapes = shell.Services.Settings.InkToShape.IsInkToShapeEnabled;

        // 4. Selection: select the freehand stroke, drag a corner handle, undo.
        controller.Tool = InkTool.Select;
        const uint selectId = 9004;
        var target = controller.Document.Strokes[0];
        controller.OnPointerDown(Sample(selectId, path[30], 0));
        controller.OnPointerUp(Sample(selectId, path[30], 10));
        bool selected = controller.Selection.Contains(target);

        var beforeBounds = target.Bounds;
        var handle = new Vector2(beforeBounds.Right, beforeBounds.Bottom);
        controller.OnPointerDown(Sample(selectId, handle, 20));
        controller.OnPointerMove(Sample(selectId, handle + new Vector2(120, 90), 60));
        controller.OnPointerUp(Sample(selectId, handle + new Vector2(120, 90), 90));
        var afterBounds = target.Bounds;
        bool scaledUp = afterBounds.Width > beforeBounds.Width + 10f;

        controller.Undo();
        var undoneBounds = target.Bounds;
        bool undoRestored = MathF.Abs(undoneBounds.Width - beforeBounds.Width) < 0.5f
                         && MathF.Abs(undoneBounds.Left - beforeBounds.Left) < 0.5f;

        checks.Add(new
        {
            check = "selection corner drag scales and undo restores",
            selected,
            scaledUp,
            undoRestored,
            widthBefore = beforeBounds.Width,
            widthAfter = afterBounds.Width,
            widthUndone = undoneBounds.Width,
        });

        // 5. Delete selection, then undo.
        controller.OnPointerDown(Sample(selectId, path[30], 100));
        controller.OnPointerUp(Sample(selectId, path[30], 110));
        int countBeforeDelete = controller.Document.Count;
        controller.DeleteSelection();
        int countAfterDelete = controller.Document.Count;
        controller.Undo();
        checks.Add(new
        {
            check = "delete selection removes and undo restores",
            removed = countBeforeDelete - countAfterDelete,
            restoredAfterUndo = controller.Document.Count == countBeforeDelete,
        });
        controller.Tool = InkTool.Pen;

        // 6. Replay produces a monotonically growing prefix and finishes complete.
        var session = new InkReplaySession(controller.Document.Strokes);
        var clock = new ReplayClock(session.Duration);
        clock.Play();
        int previousVisible = 0;
        bool monotonic = true;
        for (int i = 0; i < 40; i++)
        {
            clock.Advance((float)session.Duration.TotalMilliseconds / 20f);
            int visible = session.VisibleAt(clock.ElapsedMs).Sum(s => s.Count);
            if (visible < previousVisible) monotonic = false;
            previousVisible = visible;
        }
        checks.Add(new
        {
            check = "replay reveals strokes monotonically and completes",
            monotonic,
            completed = clock.IsComplete,
            strokes = session.StrokeCount,
        });

        // 7. Edge-gesture property can be applied to the overlay window.
        bool edgeGestureApplied = Platform.System.EdgeGestureService
            .SetEdgeGesturesDisabled(shell.Overlay.Window.Handle, true);
        Platform.System.EdgeGestureService.SetEdgeGesturesDisabled(shell.Overlay.Window.Handle, false);
        checks.Add(new { check = "edge gesture property applied", applied = edgeGestureApplied });

        // 8. GPU readback should be far below one full-screen read per frame.
        long fullFrameBytes = (long)shell.Overlay.Window.Width * shell.Overlay.Window.Height * 4;
        long frames = Math.Max(1, shell.Overlay.Presenter.PresentedFrames);
        long readback = shell.Overlay.Presenter.GpuReadbackBytes;
        checks.Add(new
        {
            check = "gpu readback stays well below full-screen-per-frame",
            fullFrameBytes,
            frames,
            readbackBytes = readback,
            naiveBytes = fullFrameBytes * frames,
            ratioWholeRun = Math.Round((double)readback / (fullFrameBytes * frames), 4),
            // Steady-state drawing is the case the optimisation targets: no commits, so the
            // committed layer is reused and only the live stroke's box is read back.
            drawingFrames,
            drawingReadbackBytesPerFrame = drawingReadback / drawingFrames,
            drawingRatioVsFullScreen = Math.Round((double)drawingReadback / drawingFrames / fullFrameBytes, 5),
        });

        // Leave the freehand stroke selected so the capture shows the scale/rotate handles
        // and the contextual delete button.
        controller.Tool = InkTool.Select;
        controller.OnPointerDown(Sample(selectId, path[30], 200));
        controller.OnPointerUp(Sample(selectId, path[30], 210));

        bool allPassed = renderingMatches && playbackWorks && erasingWorks && lassoErasingWorks && boardFeaturesWork && lassoButtonWorks && selectionWorks && boardSelectionWorks != false && chromeFits && positionStable && translucent != false && palettesFit && panelsFit && tapsWork && unfoldingWorks && popupOrder
                      && widthsBeforeCommit.SequenceEqual(widthsAfterCommit)
                      && outlineBeforeCommit.SequenceEqual(outlineAfterCommit)
                      && tipTaperedLive
                      && selected && scaledUp && undoRestored && monotonic;

        var report = new
        {
            presentedFrames = shell.Overlay.Presenter.PresentedFrames,
            latencySamples = shell.Overlay.Presenter.LatencySamples,
            lastInputToPresentMs = shell.Overlay.Presenter.LastInputToPresentMs,
            maxInputToPresentMs = shell.Overlay.Presenter.MaxInputToPresentMs,
            averageInputToPresentMs = shell.Overlay.Presenter.AverageInputToPresentMs,
            barDiagnostics = shell.BarDiagnostics,
            overlayTransparentCapable = true,
            strokeCount = controller.Document.Count,
            allCriticalChecksPassed = allPassed,
            desktopTransparencyCheckAvailable = translucent.HasValue,
            desktopTransparencyVerified = translucent == true,
            checks,
        };
        File.WriteAllText(Path.Combine(outputDirectory, "selftest.json"),
            JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static bool CheckTapInk(List<object> checks)
    {
        bool passed = true;
        foreach (var tool in new[] { InkTool.Pen, InkTool.Highlighter })
        foreach (var kind in new[] { OverlayPointerKind.Mouse, OverlayPointerKind.Pen, OverlayPointerKind.Touch })
        {
            var controller = new InkController { Tool = tool, RecognizeShapes = false };
            var sample = new OverlayPointer(9900, 50, 50, 0.5f, 1, 1, kind, 0);
            controller.OnPointerDown(sample);
            controller.OnPointerUp(sample with { TimeMs = 10 });
            controller.OnPointerUp(sample); // capture-lost after up must not duplicate the dot
            bool committed = controller.Document.Count == 1 && !controller.LiveStrokes.Any()
                && controller.Document.Strokes[0].IsCompleted
                && controller.Document.Strokes[0].Points[0].Width == controller.CurrentAttributes.BaseWidth;
            controller.FinishDotAnimations();
            controller.Undo();
            bool undo = controller.Document.Count == 0;
            controller.Redo();
            bool redo = controller.Document.Count == 1;
            passed &= committed && undo && redo;
            checks.Add(new { check = "tap commits visible dot with undo/redo", tool = tool.ToString(),
                kind = kind.ToString(), committed, undo, redo });
        }
        return passed;
    }

    private static async Task<bool> CheckUnfoldHandles(ShellController shell, List<object> checks)
    {
        var bar = shell.OpenWindows.OfType<FloatingBarWindow>().First();
        bool passed = true;
        for (int side = 0; side < 2; side++)
        {
            if (!bar.IsFolded) shell.ToggleFold();
            await Task.Delay(100);
            var button = (Button)bar.FoldHandles[side].Content;
            var peer = new ButtonAutomationPeer(button);
            ((IInvokeProvider)peer.GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(1200); // include a maintenance poll that used to re-fold the bar
            bool unfolded = !bar.IsFolded && NativeMethods.IsWindowVisible(WindowChrome.GetHwnd(bar));
            passed &= unfolded;
            checks.Add(new { check = "edge button unfolds across maintenance poll", side, unfolded });
        }
        return passed;
    }

    private static async Task<bool> CheckPopupOrder(ShellController shell, List<object> checks)
    {
        var bar = shell.OpenWindows.OfType<FloatingBarWindow>().First();
        // A separate topmost popup reproduces the Z-order contract of an IME candidate HWND.
        // This checks maintenance behavior without depending on an installed input language.
        var popup = new Window { Title = "InkCanvas popup order regression", Content = new TextBox() };
        WindowChrome.MakeBorderless(popup);
        WindowChrome.PlaceDip(popup, 50, 50, 180, 40);
        popup.Activate();
        try
        {
            await Task.Delay(1200);
            bool above = false;
            for (var h = NativeMethods.GetWindow(WindowChrome.GetHwnd(bar), 3);
                 h != IntPtr.Zero; h = NativeMethods.GetWindow(h, 3))
                if (h == WindowChrome.GetHwnd(popup)) { above = true; break; }
            checks.Add(new { check = "maintenance preserves candidate popup Z order", above });
            return above;
        }
        finally { popup.Close(); }
    }

    private static async Task<bool> CheckFloatingBarLayout(ShellController shell, List<object> checks)
    {
        var window = shell.OpenWindows.OfType<FloatingBarWindow>().FirstOrDefault();
        if (window?.Content is not FrameworkElement root) return false;
        var appearance = shell.Services.Settings.Appearance;
        double originalScale = appearance.FloatingBarScale;
        bool originalLabels = appearance.IsEnableDisPlayFloatBarText;
        bool allFit = true;
        try
        {
            foreach (bool labels in new[] { false, true })
            foreach (double scale in new[] { 50.0, 100.0, 200.0 })
            {
                appearance.FloatingBarScale = scale;
                appearance.IsEnableDisPlayFloatBarText = labels;
                window.Bar.RefreshSettingsVisuals();
                window.Reposition(appearance.FloatingBarBottomMargin, scale);
                // HWND resize notifications reach the XAML root asynchronously. Checking in
                // the resize call stack observes the previous client size, even after UpdateLayout.
                await Task.Delay(100);
                root.UpdateLayout();
                bool fits = Fits(window.Bar);
                bool regionFits = CheckWindowRegion(window, window.Bar.ChromeSurfaces);
                allFit &= fits && regionFits;
                checks.Add(new { check = "floating bar buttons fit client area", scale, labels, fits,
                    regionFits, clientWidth = root.ActualWidth, clientHeight = root.ActualHeight });
            }
        }
        finally
        {
            appearance.FloatingBarScale = originalScale;
            appearance.IsEnableDisPlayFloatBarText = originalLabels;
            window.Bar.RefreshSettingsVisuals();
            window.Reposition(appearance.FloatingBarBottomMargin, originalScale);
            root.UpdateLayout();
        }
        return allFit;

        bool Fits(DependencyObject parent)
        {
            if (parent is UIElement element && element.Visibility == Visibility.Collapsed) return true;
            if (parent is TouchIconButton button)
            {
                var bounds = button.TransformToVisual(root).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, button.ActualWidth, button.ActualHeight));
                return bounds.Width > 0 && bounds.Height > 0 && bounds.Left >= -1 && bounds.Top >= -1
                    && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
            }
            for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (!Fits(VisualTreeHelper.GetChild(parent, i))) return false;
            return true;
        }
    }

    internal static Task<bool?> CheckBackdropTransparency(ShellController shell, List<object> checks)
        => CheckBackdropTransparency(shell.OpenWindows.OfType<FloatingBarWindow>().First(), checks);

    internal static async Task<bool?> CheckBackdropTransparency(FloatingBarWindow bar, List<object> checks)
    {
        var rect = WindowChrome.GetScreenBounds(bar, bar.Bar.ChromeSurfaces.First());
        var background = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Red) };
        var window = new Window { Title = "InkCanvas translucency regression", Content = background };
        WindowChrome.MakeBorderless(window);
        WindowChrome.PlaceClient(window, rect.X - 4, rect.Y - 4, rect.Width + 8, rect.Height + 8);
        window.Activate();
        WindowChrome.EnsureAbove(bar);
        try
        {
            await Task.Delay(250);
            int x = rect.X + (int)Math.Ceiling(4 * WindowChrome.GetDpiScale(bar));
            int y = rect.Y + rect.Height / 2;
            uint red = ReadScreenPixel(x, y);
            uint controlRed = ReadScreenPixel(rect.X - 2, y);
            uint belowRed = ReadScreenPixel(rect.X + rect.Width / 2, rect.Y + rect.Height + 2);
            background.Background = new SolidColorBrush(Microsoft.UI.Colors.Blue);
            await Task.Delay(250);
            uint blue = ReadScreenPixel(x, y);
            uint controlBlue = ReadScreenPixel(rect.X - 2, y);
            uint belowBlue = ReadScreenPixel(rect.X + rect.Width / 2, rect.Y + rect.Height + 2);
            // Validate desktop readback against the uncovered solid-color test window.
            // Some remote desktops return the same white pixel for every screen coordinate.
            bool available = red != uint.MaxValue && blue != uint.MaxValue
                && controlRed != uint.MaxValue && controlBlue != uint.MaxValue
                && (int)(controlRed & 255) - (int)(controlBlue & 255) > 80
                && (int)((controlBlue >> 16) & 255) - (int)((controlRed >> 16) & 255) > 80;
            bool visible = available && Math.Abs((int)(red & 255) - (int)(blue & 255)) > 10
                && Math.Abs((int)((red >> 16) & 255) - (int)((blue >> 16) & 255)) > 10;
            bool fallback = (bar.SystemBackdrop as ChromeBackdrop)?.State != "Active";
            var brush = bar.Bar.ChromeSurfaces.First().Background as SolidColorBrush;
            uint expected = brush is null ? uint.MaxValue
                : (uint)(brush.Color.R | brush.Color.G << 8 | brush.Color.B << 16);
            bool opaqueTheme = available && brush?.Color.A == 255 && red == expected && blue == expected;
            checks.Add(new { check = "toolbar background transmits desktop colors while inactive",
                available, visible, red, blue, controlRed, controlBlue, backdrop = (bar.SystemBackdrop as ChromeBackdrop)?.State });
            checks.Add(new { check = "desktop toolbar matches its active material or opaque theme fallback",
                available, fallback, passed = available ? (bool?)(fallback ? opaqueTheme : visible) : null, expected });
            bool clean = belowRed == controlRed && belowBlue == controlBlue;
            checks.Add(new { check = "floating toolbar leaves no rectangular shadow fringe", available, passed = available ? (bool?)clean : null, belowRed, belowBlue });
            return available ? (fallback ? opaqueTheme : visible) && clean : null;
        }
        finally { window.Close(); }
    }

    private static uint ReadScreenPixel(int x, int y)
    {
        var dc = NativeMethods.GetDC(IntPtr.Zero);
        try { return dc == IntPtr.Zero ? uint.MaxValue : GetPixel(dc, x, y); }
        finally { if (dc != IntPtr.Zero) NativeMethods.ReleaseDC(IntPtr.Zero, dc); }
    }

    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr dc, int x, int y);

    internal static async Task<bool> CheckToolbarPosition(ShellController shell, List<object> checks)
    {
        var bar = shell.OpenWindows.OfType<FloatingBarWindow>().First();
        bool originalMouseMode = shell.Overlay.ClickThrough;
        bool stable = true;
        await Task.Delay(100);
        var origin = WindowChrome.GetClientPosition(bar);
        var paintedOrigin = WindowChrome.GetScreenBounds(bar, bar.Bar.ChromeSurfaces.First());
        try
        {
            for (int i = 0; i < 6; i++)
            {
                shell.ToggleClickThrough();
                await Task.Delay(100);
                var current = WindowChrome.GetClientPosition(bar);
                var painted = WindowChrome.GetScreenBounds(bar, bar.Bar.ChromeSurfaces.First());
                bool unchanged = current.X == origin.X && current.Y == origin.Y
                    && painted.X == paintedOrigin.X && painted.Y == paintedOrigin.Y;
                stable &= unchanged;
                checks.Add(new { check = "mode switch keeps toolbar origin", mouseMode = shell.Overlay.ClickThrough,
                    unchanged, x = current.X, y = current.Y });
            }
            foreach (bool highlighter in new[] { false, true })
            {
                var popup = new PenPaletteWindow(new InkController(), highlighter, bar.Bar.Theme)
                { InkAlphaChanged = _ => { } };
                popup.PlaceAboveBar(() => bar.GetToolAnchor(highlighter ? InkTool.Highlighter : InkTool.Pen), WindowChrome.GetHwnd(bar));
                popup.Activate();
                await Task.Delay(150);
                var opened = WindowChrome.GetClientPosition(bar);
                var paintedOpened = WindowChrome.GetScreenBounds(bar, bar.Bar.ChromeSurfaces.First());
                popup.Close();
                await Task.Delay(100);
                var closed = WindowChrome.GetClientPosition(bar);
                var paintedClosed = WindowChrome.GetScreenBounds(bar, bar.Bar.ChromeSurfaces.First());
                bool unchanged = opened.X == origin.X && opened.Y == origin.Y
                    && closed.X == origin.X && closed.Y == origin.Y
                    && paintedOpened.X == paintedOrigin.X && paintedOpened.Y == paintedOrigin.Y
                    && paintedClosed.X == paintedOrigin.X && paintedClosed.Y == paintedOrigin.Y;
                stable &= unchanged;
                checks.Add(new { check = "popup activation keeps toolbar origin", highlighter, unchanged });
            }
        }
        finally
        {
            if (shell.Overlay.ClickThrough != originalMouseMode) shell.ToggleClickThrough();
        }
        return stable;
    }

    private static async Task<bool> CheckPaletteLayout(ShellController shell, List<object> checks, string directory)
    {
        var bar = shell.OpenWindows.OfType<FloatingBarWindow>().First();
        bool allFit = true;
        foreach (int theme in new[] { 1, 2 })
        foreach (bool highlighter in new[] { false, true })
        foreach (double scale in new[] { 50.0, 100.0, 200.0 })
        {
            using var previewController = new InkController();
            previewController.CurrentAttributes.Color = highlighter ? InkColor.Opaque(234, 179, 8) : InkColor.Black;
            var window = new PenPaletteWindow(previewController, highlighter, theme, scale)
            { InkAlphaChanged = alpha => previewController.CurrentAttributes.HighlighterAlpha = (byte)alpha };
            window.Activate();
            window.PlaceAboveBar(() => bar.GetToolAnchor(highlighter ? InkTool.Highlighter : InkTool.Pen), WindowChrome.GetHwnd(bar));
            try
            {
                await Task.Delay(200);
                var root = (FrameworkElement)window.Content;
                root.UpdateLayout();
                var palette = Descendants(root).OfType<PenPalette>().Single();
                var card = palette.CardSurface;
                var bounds = card.TransformToVisual(root).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, card.ActualWidth, card.ActualHeight));
                bool fits = bounds.Left >= -1 && bounds.Top >= -1
                    && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
                bool swatchesFit = Descendants(card).OfType<Button>()
                    .Where(b => b.Content is Border)
                    .All(b =>
                    {
                        var face = (Border)b.Content;
                        var faceBounds = face.TransformToVisual(b).TransformBounds(
                            new Windows.Foundation.Rect(0, 0, face.ActualWidth, face.ActualHeight));
                        return faceBounds.Left >= -0.5 && faceBounds.Top >= -0.5
                            && faceBounds.Right <= b.ActualWidth + 0.5 && faceBounds.Bottom <= b.ActualHeight + 0.5
                            && b.ActualWidth + b.Margin.Left + b.Margin.Right <= 44.5;
                    });
                var preview = Descendants(palette).OfType<PenStrokePreview>().Single(item => item.ShowChecks);
                var previewBounds = preview.TransformToVisual(card).TransformBounds(new Windows.Foundation.Rect(0, 0, preview.ActualWidth, preview.ActualHeight));
                bool previewFits = preview.ActualWidth > 0 && previewBounds.Left >= 0 && previewBounds.Top >= 0
                    && previewBounds.Right <= card.ActualWidth + 1 && previewBounds.Bottom <= card.ActualHeight + 1;
                if (scale == 100)
                {
                    var widthSlider = Descendants(palette).OfType<Slider>().Single(slider =>
                        AutomationProperties.GetName(slider).StartsWith(highlighter ? "荧光笔粗细" : "画笔粗细"));
                    widthSlider.Value = 2;
                    float thinHeight = preview.Stroke.Bounds.Height;
                    widthSlider.Value = 30;
                    bool widthUpdates = preview.Stroke.Attributes.BaseWidth == 15 && preview.Stroke.Bounds.Height > thinHeight;
                    var nib = Descendants(palette).OfType<SegmentedSelector>().Single(combo => AutomationProperties.GetName(combo) == "笔尖");
                    nib.SelectedIndex = 1;
                    bool nibUpdates = preview.Stroke.Attributes.Nib == PenNib.Ellipse45;
                    bool taperUpdates = true;
                    if (!highlighter)
                    {
                        var style = Descendants(palette).OfType<PenStylePicker>().Single();
                        style.SelectedIndex = 2;
                        taperUpdates = preview.Stroke.TaperKind == TaperKind.None && preview.Stroke.Points.All(point => point.Width == 15);
                    }
                    bool alphaUpdates = true;
                    if (highlighter)
                    {
                        var alpha = Descendants(palette).OfType<Slider>().Single(slider => AutomationProperties.GetName(slider).StartsWith("荧光笔透明度"));
                        alpha.Value = 240;
                        alphaUpdates = preview.Stroke.Attributes.EffectiveColor.A == 240 && preview.Stroke.Attributes.IsHighlighter;
                    }
                    bool sourceUntouched = previewController.Document.Count == 0 && previewController.History.UndoCount == 0;
                    allFit &= widthUpdates && nibUpdates && taperUpdates && alphaUpdates && sourceUntouched;
                    checks.Add(new { check = "pen preview follows width nib taper and alpha without editing canvas", theme, highlighter, widthUpdates, nibUpdates, taperUpdates, alphaUpdates, sourceUntouched });
                    await BoardWindowRegression.SaveVisual(palette, Path.Combine(directory, $"pen-preview-{theme}-{highlighter}.png"));
                }
                allFit &= previewFits;
                bool regionFits = CheckWindowRegion(window, new[] { card });
                var anchor = bar.GetToolAnchor(highlighter ? InkTool.Highlighter : InkTool.Pen);
                var painted = WindowChrome.GetScreenBounds(window, card);
                // Win32 minimum tracking dimensions can exceed the painted card at 50%.
                // Alignment is defined by what is visible, not by that clipped native padding.
                var expected = WindowChrome.GetPopupPosition(anchor, painted.Width,
                    painted.Height, (int)Math.Ceiling(8 * WindowChrome.GetDpiScale(bar)));
                bool anchored = Math.Abs(painted.X - expected.X) <= 1 && Math.Abs(painted.Y - expected.Y) <= 1;
                allFit &= fits && swatchesFit && regionFits && anchored;
                checks.Add(new { check = "palette card and swatches fit without backdrop margins",
                    theme, highlighter, scale, fits, swatchesFit, regionFits, anchored,
                    paintedX = painted.X, paintedY = painted.Y, expectedX = expected.X, expectedY = expected.Y });
            }
            finally { window.Close(); }
        }
        return allFit;
    }

    private static async Task<bool> CheckPopupLayout(ShellController shell, List<object> checks)
    {
        var bar = shell.OpenWindows.OfType<FloatingBarWindow>().First();
        double originalScale = shell.Services.Settings.Appearance.FloatingBarScale;
        bool allFit = true;
        try
        {
            foreach (double scale in new[] { 50.0, 100.0, 200.0 })
            foreach (string kind in new[] { "more", "gesture", "shapes", "eraser" })
            {
                bar.Reposition(shell.Services.Settings.Appearance.FloatingBarBottomMargin, scale);
                Window window;
                if (kind == "shapes")
                {
                    window = new ShapePickerWindow(new InkController(), () => { }, bar.Bar.Theme, scale);
                    window.Activate();
                }
                else
                {
                    var panel = new BarPanelWindow(bar, bar.Bar, kind switch { "more" => BarPanelKind.More, "eraser" => BarPanelKind.Eraser, _ => BarPanelKind.Gesture });
                    panel.ShowAboveBar(bar.AppWindow.Position.X, bar.AppWindow.Position.Y, bar.AppWindow.Size.Width);
                    window = panel;
                }
                try
                {
                    await Task.Delay(200);
                    var root = (FrameworkElement)window.Content;
                    var card = window is ShapePickerWindow
                        ? (Border)Descendants(root).OfType<ShapePreview>().Single().Content
                        : (Border)((Viewbox)root).Child;
                    var bounds = card.TransformToVisual(root).TransformBounds(
                        new Windows.Foundation.Rect(0, 0, card.ActualWidth, card.ActualHeight));
                    bool fits = bounds.Left >= -1 && bounds.Top >= -1
                        && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
                    bool regionFits = CheckWindowRegion(window, new[] { card });
                    allFit &= fits && regionFits;
                    checks.Add(new { check = "popup content fits without backdrop margins", kind, scale, fits, regionFits });
                }
                finally { window.Close(); }
            }
        }
        finally { bar.Reposition(shell.Services.Settings.Appearance.FloatingBarBottomMargin, originalScale); }
        return allFit;
    }

    private static async Task<bool> CheckLassoButton(ShellController shell, List<object> checks, string directory)
    {
        var window = shell.OpenWindows.OfType<FloatingBarWindow>().First();
        var controller = shell.Services.OverlayInk;
        var originalTool = controller.Tool;
        var originalMode = controller.EraserMode;
        bool originalMouseMode = shell.Overlay.ClickThrough;
        try
        {
            if (originalMouseMode) shell.ToggleClickThrough();
            await Task.Delay(50);
            var button = Descendants(window.Bar).OfType<TouchIconButton>().Single(b => b.Label == "橡皮");
            var origin = WindowChrome.GetClientPosition(window);
            bool passed = true;
            foreach (var (mode, label) in new[] { (InkEraserMode.Area, "面积擦"), (InkEraserMode.WholeStroke, "墨迹擦"), (InkEraserMode.Lasso, "套索擦") })
            {
                ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
                await Task.Delay(80);
                var menu = window.GetOpenPanel(BarPanelKind.Eraser);
                var option = Descendants(menu.Content).OfType<TouchIconButton>().Single(b => b.Label == label);
                if (mode == InkEraserMode.Lasso)
                    await BoardWindowRegression.SaveVisual((FrameworkElement)menu.Content, Path.Combine(directory, "desktop-eraser-menu.png"));
                ((IInvokeProvider)new ButtonAutomationPeer(option).GetPattern(PatternInterface.Invoke)).Invoke();
                await Task.Delay(30);
                var position = WindowChrome.GetClientPosition(window);
                bool activated = controller.Tool == InkTool.Eraser && controller.EraserMode == mode
                    && button.IsSelected && !shell.Overlay.ClickThrough;
                bool menuClosed = window.GetOpenPanel(BarPanelKind.Eraser) is null;
                bool stable = position.X == origin.X && position.Y == origin.Y;
                passed &= activated && menuClosed && stable;
                checks.Add(new { check = "floating bar eraser menu chooses mode without shifting toolbar", mode = mode.ToString(), activated, menuClosed, stable });
            }
            return passed;
        }
        finally
        {
            window.ClosePanels();
            controller.EraserMode = originalMode;
            controller.Tool = originalTool;
            if (shell.Overlay.ClickThrough != originalMouseMode) shell.ToggleClickThrough();
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }

    internal static bool CheckWindowRegion(Window window, IEnumerable<Border> surfaces)
    {
        var hwnd = WindowChrome.GetHwnd(window);
        var region = CreateRectRgn(0, 0, 0, 0);
        try
        {
            if (GetWindowRgn(hwnd, region) == 0 || !GetWindowRect(hwnd, out var frame)) return false;
            var origin = new NativeMethods.POINT();
            if (!ClientToScreen(hwnd, ref origin)) return false;
            var root = (FrameworkElement)window.Content;
            double dpi = root.XamlRoot.RasterizationScale;
            var rects = surfaces.Where(s => s.Visibility == Visibility.Visible && s.ActualWidth > 0)
                .Select(s => s.TransformToVisual(root).TransformBounds(
                    new Windows.Foundation.Rect(0, 0, s.ActualWidth, s.ActualHeight))).ToArray();
            foreach (var r in rects)
            {
                int cx = origin.X - frame.Left + (int)((r.Left + r.Width / 2) * dpi);
                int cy = origin.Y - frame.Top + (int)((r.Top + r.Height / 2) * dpi);
                if (!PtInRegion(region, cx, cy)) return false;
                int left = origin.X - frame.Left + (int)Math.Ceiling(r.Left * dpi - 0.5);
                int top = origin.Y - frame.Top + (int)Math.Ceiling(r.Top * dpi - 0.5);
                int right = origin.X - frame.Left + (int)Math.Ceiling(r.Right * dpi - 0.5) - 1;
                int bottom = origin.Y - frame.Top + (int)Math.Ceiling(r.Bottom * dpi - 0.5) - 1;
                if (!PtInRegion(region, left, cy) || !PtInRegion(region, right, cy)
                    || !PtInRegion(region, cx, top) || !PtInRegion(region, cx, bottom)) return false;
            }
            // Sample native pixels outside every painted card, including gaps and minimum-size
            // padding. They must be excluded from both rendering and native hit testing.
            for (int y = 0; y < frame.Bottom - frame.Top; y += 2)
            for (int x = 0; x < frame.Right - frame.Left; x += 2)
            {
                double lx = (x - origin.X + frame.Left) / dpi;
                double ly = (y - origin.Y + frame.Top) / dpi;
                bool insideCard = rects.Any(r => lx >= r.Left - 1 && lx <= r.Right + 1
                    && ly >= r.Top - 1 && ly <= r.Bottom + 1);
                if (!insideCard && PtInRegion(region, x, y)) return false;
            }
            return true;
        }
        finally { NativeMethods.DeleteObject(region); }
    }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);
    [DllImport("user32.dll")] private static extern int GetWindowRgn(IntPtr hwnd, IntPtr region);
    [DllImport("gdi32.dll")] private static extern bool PtInRegion(IntPtr region, int x, int y);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out NativeMethods.RECT rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativeMethods.POINT point);

    private static OverlayPointer Sample(uint id, Vector2 p, uint timeMs)
        => new(id, p.X, p.Y, 0.5f, 0, 0, OverlayPointerKind.Pen, timeMs);
}
