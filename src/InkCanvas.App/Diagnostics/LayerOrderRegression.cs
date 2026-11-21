using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics.DirectX;

namespace InkCanvas.App.Diagnostics;

internal static class LayerOrderRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services);
        shell.Start(); shell.StartInClickThrough();
        try
        {
            var device = CanvasDevice.GetSharedDevice();
            string imagePath = Path.Combine(directory, "blue.png");
            using (var bitmap = new CanvasRenderTarget(device, 300, 160, 96))
            {
                using (var draw = bitmap.CreateDrawingSession()) draw.Clear(Microsoft.UI.Colors.RoyalBlue);
                await bitmap.SaveAsync(imagePath, CanvasBitmapFileFormat.Png);
            }
            foreach (bool boardMode in new[] { false, true })
            {
                if (boardMode) shell.ToggleBoard();
                await Task.Delay(180);
                var board = shell.OpenWindows.OfType<BoardWindow>().FirstOrDefault();
                var store = boardMode ? services.Board : services.ScreenImages;
                var controller = store.Current;
                controller.Clear(); controller.ViewTransform = Matrix3x2.Identity;
                var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 80, TaperKind = TaperKind.None,
                    Color = InkColor.Opaque(230, 30, 40) });
                stroke.AddPoint(200, 300); stroke.AddPoint(500, 300); stroke.Complete();
                controller.Document.Add(stroke);
                var image = new BoardImageAsset { Name = "blue", Data = File.ReadAllBytes(imagePath),
                    Left = 200, Top = 220, Width = 300, Height = 160 };
                if (boardMode) board.InsertImage(image); else shell.InsertDesktopImage(image);
                controller.ClearSelection(); controller.Selection.Add(stroke); controller.NotifyElementsChanged();
                controller.History.Clear();
                if (!boardMode) shell.Overlay.Presenter.Scene.ShowSelectionOutline = false;
                await Task.Delay(160);
                var toolbar = boardMode ? Descendants(board.Content).OfType<SelectionToolbar>().Single()
                    : shell.OpenWindows.OfType<SelectionToolbarWindow>().Single().Toolbar;
                toolbar.Refresh();
                Invoke(Descendants(toolbar).OfType<TouchIconButton>().Single(button => button.Label == "层级"));
                await Task.Delay(160);
                Check("four layer controls are available " + boardMode, new[] { "置于顶层", "上移一层", "下移一层", "置于底层" }
                    .All(label => Descendants(toolbar).OfType<TouchIconButton>().Any(button => button.Label == label)));
                await VerifyPixels("initial image above ink", red: false);
                foreach (var (label, red) in new[] { ("上移一层", true), ("下移一层", false), ("置于顶层", true), ("置于底层", false) })
                {
                    var button = Descendants(toolbar).OfType<TouchIconButton>().Single(button => button.Label == label);
                    Check("layer action enabled " + boardMode + " " + label, button.IsEnabled);
                    Invoke(button);
                    await Task.Delay(80);
                    await VerifyPixels(label, red);
                    Check("selection survives layer action " + boardMode + " " + label, controller.Selection.Count == 1
                        && ReferenceEquals(controller.Selection[0], stroke) && controller.SelectedElements.Count == 0);
                }
                Check("one undo step per layer change " + boardMode, controller.History.UndoCount == 4);
                controller.Undo(); toolbar.Refresh();
                await VerifyPixels("undo restores front ink", red: true);
                Check("front boundary is disabled " + boardMode,
                    !Descendants(toolbar).OfType<TouchIconButton>().Single(button => button.Label == "置于顶层").IsEnabled);
                controller.Redo(); toolbar.Refresh();
                await VerifyPixels("redo restores back ink", red: false);
                Check("back boundary is disabled " + boardMode,
                    !Descendants(toolbar).OfType<TouchIconButton>().Single(button => button.Label == "下移一层").IsEnabled);
                var menuRoot = boardMode ? (FrameworkElement)board.Content : (FrameworkElement)shell.OpenWindows.OfType<SelectionToolbarWindow>().Single().Content;
                Check("layer controls fit toolbar " + boardMode, Descendants(toolbar).OfType<TouchIconButton>()
                    .Where(button => button.Label is "上移一层" or "下移一层" or "置于顶层" or "置于底层").All(button =>
                    {
                        var bounds = button.TransformToVisual(menuRoot).TransformBounds(new Windows.Foundation.Rect(0, 0, button.ActualWidth, button.ActualHeight));
                        return bounds.Left >= 0 && bounds.Top >= 0 && bounds.Right <= menuRoot.ActualWidth + 1 && bounds.Bottom <= menuRoot.ActualHeight + 1;
                    }));
                await BoardWindowRegression.SaveVisual(toolbar, Path.Combine(directory, $"layer-menu-{boardMode}.png")).WaitAsync(TimeSpan.FromSeconds(8));
                string page = Path.Combine(directory, $"layers-{boardMode}.icink");
                store.SavePage(page);
                var loaded = new BoardService(); loaded.LoadPage(page);
                Check("saved page keeps mixed layer order " + boardMode, loaded.Current.DrawingObjects[0].Stroke is not null
                    && loaded.Current.DrawingObjects[1].Element is IImageSelectionElement);
                controller.SelectedElements.Add(store.GetSelectionElements().Single());
                controller.ArrangeSelection(LayerAction.BringToFront);
                Check("selecting every object adds no history " + boardMode, controller.History.UndoCount == 4);
                controller.ClearSelection();
                if (boardMode)
                {
                    var media = new BoardMediaAsset { Data = new byte[] { 1 }, Left = 600, Top = 200, Width = 200, Height = 120 };
                    store.AddMedia(media);
                    await Task.Delay(180);
                    var mediaElement = store.GetSelectionElements().Single(element => element.Id == media.Id);
                    controller.SelectElement(mediaElement);
                    controller.ArrangeSelection(LayerAction.SendToBack);
                    await Task.Delay(100);
                    var layers = Descendants(board.Content).OfType<BoardDrawingLayers>().Single();
                    var mediaView = layers.Children.OfType<Grid>().Single(view => view.Tag is BoardMediaAsset);
                    var inkView = layers.Children.OfType<CanvasControl>().Single();
                    Check("video view shares ordering with cached ink and images", Canvas.GetZIndex(mediaView) < Canvas.GetZIndex(inkView));
                    controller.ArrangeSelection(LayerAction.BringToFront);
                    Check("video view moves above cached drawing without recreating the player", ReferenceEquals(mediaView,
                        layers.Children.OfType<Grid>().Single(view => view.Tag is BoardMediaAsset)) && Canvas.GetZIndex(mediaView) > Canvas.GetZIndex(inkView));
                    controller.ArrangeSelection(LayerAction.MoveBackward);
                    await Task.Delay(80);
                    var middleLayers = layers.Children.OfType<CanvasControl>().OrderBy(Canvas.GetZIndex).ToArray();
                    Check("video can sit between an ink run and an image run", middleLayers.Length == 2
                        && Canvas.GetZIndex(middleLayers[0]) < Canvas.GetZIndex(mediaView)
                        && Canvas.GetZIndex(mediaView) < Canvas.GetZIndex(middleLayers[1]));
                    controller.Undo();
                    Check("undo merges cached runs and preserves the media view", layers.Children.OfType<CanvasControl>().Count() == 1
                        && ReferenceEquals(mediaView, layers.Children.OfType<Grid>().Single(view => view.Tag is BoardMediaAsset)));
                    controller.ClearSelection(); controller.Selection.Add(stroke);
                    controller.SelectedElements.Add(store.GetSelectionElements().Single(element => element.Id == image.Id));
                    await board.ExportSelectionImageAsync(Path.Combine(directory, "mixed-export.png")).WaitAsync(TimeSpan.FromSeconds(8));
                    shell.ToggleBoard();
                    Check("board closes cleanly after mixed layer edits", !shell.OpenWindows.OfType<BoardWindow>().Any());
                }

                async Task VerifyPixels(string name, bool red)
                {
                    byte[] pixels;
                    int width;
                    if (boardMode)
                    {
                        var layers = Descendants(board.Content).OfType<BoardDrawingLayers>().Single();
                        Check("board cached layer fills viewport " + name, layers.ActualWidth > 700 && layers.ActualHeight > 400
                            && layers.Children.OfType<CanvasControl>().All(canvas => canvas.ActualWidth > 700 && canvas.ActualHeight > 400));
                        string path = Path.Combine(directory, $"board-{name.Replace(' ', '-')}.png");
                        await layers.SaveSceneAsync(path);
                        using var bitmap = await CanvasBitmap.LoadAsync(device, path);
                        pixels = bitmap.GetPixelBytes(); width = (int)bitmap.SizeInPixels.Width;
                    }
                    else
                    {
                        await shell.Overlay.Presenter.Scene.Images.PrepareAsync(device, controller);
                        controller.NotifyElementsChanged();
                        var window = shell.Overlay.Window;
                        width = window.Width;
                        pixels = new byte[width * window.Height * 4];
                        Marshal.Copy(window.PixelBuffer, pixels, 0, pixels.Length);
                    }
                    float scale = boardMode ? 1 : shell.Overlay.Window.DpiScale;
                    int offset = ((int)(300 * scale) * width + (int)(350 * scale)) * 4;
                    Check("rendered layer color " + boardMode + " " + name, red
                        ? pixels[offset + 2] > 200 && pixels[offset] < 60
                        : pixels[offset] > 200 && pixels[offset + 2] < 100);
                }
            }
        }
        catch (Exception ex) { checks.Add(new { check = "layer order exception", passed = false, error = ex.ToString() }); }
        finally
        {
            File.WriteAllText(Path.Combine(directory, "layer-order.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var window in shell.OpenWindows.ToArray()) window.Close();
            Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }
}
