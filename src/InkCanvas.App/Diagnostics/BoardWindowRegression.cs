using System.Numerics;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace InkCanvas.App.Diagnostics;

internal static class BoardWindowRegression
{
    public static async Task<bool> Run(BoardWindow window, AppServices services, List<object> checks, string directory)
    {
        bool passed = true;
        var controller = window.CurrentController;
        var root = (FrameworkElement)window.Content;
        foreach (double scale in new[] { 50d, 100d, 150d })
        {
            services.Settings.Appearance.BlackboardScale = scale; window.RefreshUiScale();
            await Task.Delay(80);
            var rects = window.DockSurfaces.Select(s => s.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, s.ActualWidth, s.ActualHeight))).ToArray();
            bool fits = rects.All(r => r.Width > 0 && r.Left >= -1 && r.Right <= root.ActualWidth + 1 && r.Top >= -1 && r.Bottom <= root.ActualHeight + 1);
            bool separate = true;
            for (int i = 0; i < rects.Length; i++)
            for (int j = i + 1; j < rects.Length; j++)
                separate &= rects[i].Right <= rects[j].Left + 1 || rects[j].Right <= rects[i].Left + 1
                    || rects[i].Bottom <= rects[j].Top + 1 || rects[j].Bottom <= rects[i].Top + 1;
            passed &= fits && separate;
            checks.Add(new { check = "board dock strips fit without overlap", scale, fits, separate });
        }
        services.Settings.Appearance.BlackboardScale = 100; window.RefreshUiScale();
        foreach (var background in new[] { BoardBackground.Black, BoardBackground.White, BoardBackground.Black, BoardBackground.White })
        {
            controller.Tool = InkTool.Pen;
            services.Board.SetBackground(background);
            await Task.Delay(30);
            var selected = Descendants(root).OfType<Button>().Where(b => AutomationProperties.GetItemStatus(b) == "已选中").ToArray();
            bool oneActive = selected.Length == 1 && Descendants(selected[0]).OfType<TextBlock>().Any(t => t.Text == "墨迹");
            var otherTool = Descendants(root).OfType<Button>().First(b => Descendants(b).OfType<TextBlock>().Any(t => t.Text == "橡皮"));
            bool differentFill = selected.Length == 1 && ((SolidColorBrush)((Border)selected[0].Content).Background).Color != ((SolidColorBrush)((Border)otherTool.Content).Background).Color;
            passed &= oneActive && differentFill;
            checks.Add(new { check = "board theme change preserves one active tool", background = background.ToString(), oneActive, differentFill });
        }
        foreach (int theme in new[] { 1, 2 })
        {
            var settings = new InkCanvas.Core.Settings.GestureSettings { IsEnableMultiTouchMode = true };
            var panel = new BoardGesturePanel(settings, theme);
            var settingsWindow = new Window { Title = "手势面板回归", Content = panel };
            WindowChrome.MakeBorderless(settingsWindow); WindowChrome.CenterOnPrimary(settingsWindow, 314, 400);
            WindowChrome.ApplyTheme(settingsWindow, theme); settingsWindow.Activate(); await Task.Delay(100);
            var toggles = Descendants(panel).OfType<ToggleSwitch>().ToArray();
            bool disabledDuringWriting = toggles.Skip(1).All(t => !t.IsEnabled);
            toggles[0].IsOn = false;
            bool enableGestures = !settings.IsEnableMultiTouchMode && toggles.Skip(1).All(t => t.IsEnabled);
            toggles[3].IsOn = true;
            var center = Descendants(panel).OfType<ComboBox>().Single(); center.SelectedIndex = 2;
            bool optionsWork = settings.IsEnableTwoFingerRotation && (int)settings.MatrixTransformCenterPoint == 2;
            bool fits = toggles.All(t =>
            {
                var r = t.TransformToVisual(panel).TransformBounds(new Windows.Foundation.Rect(0, 0, t.ActualWidth, t.ActualHeight));
                return r.Left >= 0 && r.Right <= panel.ActualWidth + 1;
            });
            passed &= disabledDuringWriting && enableGestures && optionsWork && fits;
            checks.Add(new { check = "board gesture panel layout and settings", theme, disabledDuringWriting, enableGestures, optionsWork, fits });
            await SaveVisual(panel, Path.Combine(directory, $"board-gestures-{theme}.png")); settingsWindow.Close();
        }
        foreach (var (mode, label) in new[] { (InkEraserMode.Area, "面积擦"), (InkEraserMode.WholeStroke, "墨迹擦"), (InkEraserMode.Lasso, "套索擦") })
        {
            window.ShowEraserPalette(); await Task.Delay(80);
            var erasers = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).SelectMany(p => Descendants(p.Child)).OfType<EraserPalette>().Single();
            var option = Descendants(erasers).OfType<TouchIconButton>().Single(button => button.Label == label);
            if (mode == InkEraserMode.Lasso) await SaveVisual((FrameworkElement)erasers.Parent, Path.Combine(directory, "board-eraser-menu.png"));
            Invoke(option); await Task.Delay(30);
            bool activated = controller.Tool == InkTool.Eraser && controller.EraserMode == mode;
            bool menuClosed = !VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).SelectMany(p => Descendants(p.Child)).OfType<EraserPalette>().Any();
            passed &= activated && menuClosed;
            checks.Add(new { check = "board eraser submenu chooses mode", mode = mode.ToString(), activated, menuClosed });
        }
        foreach (var background in new[] { BoardBackground.Black, BoardBackground.White })
        {
            services.Board.SetBackground(background);
            window.ShowPenPalette(); await Task.Delay(100);
            var palette = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).SelectMany(p => Descendants(p.Child)).OfType<BoardPenPalette>().Single();
            var swatches = Descendants(palette).OfType<Button>().Where(b => !string.IsNullOrEmpty(AutomationProperties.GetName(b))).ToArray();
            bool structure = swatches.Length == 16 && Descendants(palette).OfType<ToggleSwitch>().Count() == 2
                && Descendants(palette).OfType<Slider>().Count() == 3;
            bool fit = Descendants(palette).OfType<Button>().All(b =>
            {
                var r = b.TransformToVisual(palette).TransformBounds(new Windows.Foundation.Rect(0, 0, b.ActualWidth, b.ActualHeight));
                return r.Left >= -1 && r.Top >= -1 && r.Right <= palette.ActualWidth + 1 && r.Bottom <= palette.ActualHeight + 1;
            });
            var previews = Descendants(palette).OfType<PenStrokePreview>().Where(item => item.ShowChecks).ToArray();
            var widthSlider = Descendants(palette).OfType<Slider>().Single(slider => AutomationProperties.GetName(slider) == "画笔粗细");
            double previousWidth = widthSlider.Value;
            widthSlider.Value = 30;
            var highlighterWidthSlider = Descendants(palette).OfType<Slider>().Single(slider => AutomationProperties.GetName(slider) == "荧光笔粗细");
            highlighterWidthSlider.Value = 120;
            bool previewUpdates = previews.Length == 2
                && previews.Single(p => !p.Stroke.Attributes.IsHighlighter).Stroke.Attributes.BaseWidth == 15
                && previews.Single(p => p.Stroke.Attributes.IsHighlighter).Stroke.Attributes.BaseWidth == 60;
            widthSlider.Value = previousWidth;
            bool previewFits = previews.All(p =>
            {
                var r = p.TransformToVisual(palette).TransformBounds(new Windows.Foundation.Rect(0, 0, p.ActualWidth, p.ActualHeight));
                return r.Left >= -1 && r.Top >= -1 && r.Right <= palette.ActualWidth + 1 && r.Bottom <= palette.ActualHeight + 1;
            });
            passed &= previewUpdates && previewFits;
            checks.Add(new { check = "board pen and highlighter previews follow independent widths", background = background.ToString(), previewUpdates, previewFits });
            await SaveVisual(palette, Path.Combine(directory, $"board-ink-options-{background}.png"));
            var highlighter = swatches.Single(b => AutomationProperties.GetName(b) == "荧光笔红色");
            Invoke(highlighter); await Task.Delay(40);
            bool colorPick = controller.Tool == InkTool.Highlighter;
            passed &= structure && fit && colorPick;
            checks.Add(new { check = "WPF combined board ink palette", background = background.ToString(), structure, fit, colorPick });
        }
        services.Board.SetBackground(BoardBackground.White);
        var device = CanvasDevice.GetSharedDevice();
        async Task<byte[]> ColorBytes(string color, Windows.UI.Color value)
        {
            string path = Path.Combine(directory, $"board-sample-{color}.png");
            using var tile = new CanvasRenderTarget(device, 8, 8, 96);
            using (var drawing = tile.CreateDrawingSession()) drawing.Clear(value);
            await tile.SaveAsync(path, CanvasBitmapFileFormat.Png);
            return await File.ReadAllBytesAsync(path);
        }
        var red = new BoardImageAsset { Data = await ColorBytes("red", Microsoft.UI.Colors.Red), Left = 300, Top = 100, Width = 40, Height = 40 };
        var blue = new BoardImageAsset { Data = await ColorBytes("blue", Microsoft.UI.Colors.Blue), Left = 400, Top = 100, Width = 40, Height = 40 };
        services.Board.AddImage(red); services.Board.AddImage(blue);
        controller.SelectAll();
        bool allSelected = controller.Selection.Count == 1 && controller.SelectedElements.Count == 2;
        var bounds = controller.SelectionBounds;
        string export = Path.Combine(directory, "board-multiple-selection.png");
        await window.ExportSelectionImageAsync(export);
        using (var bitmap = await CanvasBitmap.LoadAsync(device, export))
        {
            var redPixel = bitmap.GetPixelColors((int)(320 - bounds.Left), (int)(120 - bounds.Top), 1, 1)[0];
            var bluePixel = bitmap.GetPixelColors((int)(420 - bounds.Left), (int)(120 - bounds.Top), 1, 1)[0];
            bool bothImages = redPixel.R > 200 && redPixel.B < 30 && bluePixel.B > 200 && bluePixel.R < 30;
            passed &= allSelected && bothImages;
            checks.Add(new { check = "board multi-selection exports every image", allSelected, bothImages });
        }
        await SaveVisual(root, Path.Combine(directory, "board-white.png"));
        var originals = controller.Document.Strokes.ToArray();
        int undoBefore = controller.History.UndoCount;
        controller.BeginPlayback();
        var replay = new ReplayWindow(controller, 1);
        replay.Activate(); await Task.Delay(80);
        replay.Seek((float)controller.Playback.Duration.TotalMilliseconds / 2);
        bool inPlace = ReferenceEquals(replay.TargetController, controller) && !Descendants((FrameworkElement)replay.Content).OfType<InkSurface>().Any()
            && controller.Document.Strokes.SequenceEqual(originals) && controller.History.UndoCount == undoBefore;
        await SaveVisual((FrameworkElement)replay.Content, Path.Combine(directory, "replay-floating-controls.png"));
        await SaveVisual(root, Path.Combine(directory, "board-replaying.png"));
        replay.Close();
        bool replayRestored = controller.Playback is null && controller.Document.Strokes.SequenceEqual(originals);
        passed &= inPlace && replayRestored;
        checks.Add(new { check = "floating playback controls reuse board and restore original ink", inPlace, replayRestored });
        var moreButton = Descendants(root).OfType<Button>().First(b => Descendants(b).OfType<TextBlock>().Any(t => t.Text == "更多"));
        Invoke(moreButton); await Task.Delay(100);
        var popup = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot)
            .Single(p => Descendants(p.Child).OfType<TextBlock>().Any(t => t.Text == "更多功能"));
        bool launchArtwork = Descendants(popup.Child).OfType<Image>().Count() == 2;
        passed &= launchArtwork;
        checks.Add(new { check = "board more menu retains original WPF launcher artwork", launchArtwork });
        var moreContent = Descendants(popup.Child).OfType<Viewbox>().First(v => v.Child is Border);
        bool previewAvailable = await SaveVisual((FrameworkElement)moreContent.Child, Path.Combine(directory, "board-more.png"));
        checks.Add(new { check = "board more menu preview", previewAvailable });
        Invoke(moreButton); await Task.Delay(50);
        controller.ClearSelection(); services.Board.RemoveImage(red); services.Board.RemoveImage(blue); controller.History.Clear();
        controller.Tool = InkTool.Pan;
        var pointer = new OverlayPointer(9980, 100, 100, .5f, 0, 0, OverlayPointerKind.Mouse, 0);
        controller.OnPointerDown(pointer); controller.OnPointerUp(pointer with { X = 145, Y = 125 });
        bool pan = controller.Document.Count == 1 && controller.ViewTransform.M31 == 45 && controller.ViewTransform.M32 == 25 && controller.History.UndoCount == 0;
        controller.ViewTransform = Matrix3x2.Identity; controller.Tool = InkTool.Pen;
        passed &= pan;
        checks.Add(new { check = "single finger board pan moves viewport without editing ink", pan });
        return passed;
    }

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    internal static async Task<bool> SaveVisual(FrameworkElement element, string path)
    {
        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var buffer = await bitmap.GetPixelsAsync();
        if (bitmap.PixelWidth <= 0 || bitmap.PixelHeight <= 0 || buffer.Length == 0) return false;
        var bytes = new byte[buffer.Length];
        using (var reader = DataReader.FromBuffer(buffer)) reader.ReadBytes(bytes);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, (uint)bitmap.PixelWidth, (uint)bitmap.PixelHeight, 96, 96, bytes);
        await encoder.FlushAsync(); stream.Seek(0);
        using var output = File.Create(path);
        await System.IO.WindowsRuntimeStreamExtensions.AsStreamForRead(stream).CopyToAsync(output);
        return true;
    }
}
