using System.Numerics;
using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Selection;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.Windows;
using InkCanvas.Platform.Interop;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.Storage;

namespace InkCanvas.App.Diagnostics;

internal static class ImageObjectsRegression
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
        bool cursorAvailable = NativeMethods.GetCursorPos(out var originalCursor);
        try
        {
            var device = CanvasDevice.GetSharedDevice();
            string sample = Path.Combine(directory, "sample.png");
            using (var image = new CanvasRenderTarget(device, 240, 160, 96))
            {
                using (var draw = image.CreateDrawingSession())
                {
                    draw.Clear(Microsoft.UI.Colors.CornflowerBlue);
                    draw.FillRectangle(20, 20, 70, 120, Microsoft.UI.Colors.OrangeRed);
                }
                await image.SaveAsync(sample, CanvasBitmapFileFormat.Png);
            }
            foreach (bool board in new[] { false, true })
            {
                if (board) shell.ToggleBoard();
                await Task.Delay(180);
                var controller = board ? services.Board.Current : services.OverlayInk;
                if (!board) shell.Overlay.Presenter.Scene.ShowSelectionOutline = false;
                var store = board ? services.Board : services.ScreenImages;
                controller.ViewTransform = Matrix3x2.CreateScale(1.2f) * Matrix3x2.CreateTranslation(43.25f, -17.75f);
                var boardWindow = shell.OpenWindows.OfType<BoardWindow>().FirstOrDefault();
                var viewport = new Vector2(shell.Overlay.Window.Width / shell.Overlay.Window.DpiScale,
                    shell.Overlay.Window.Height / shell.Overlay.Window.DpiScale);
                var asset = await ImageImport.LoadAsync(await StorageFile.GetFileFromPathAsync(sample), true, viewport, controller.ViewTransform);
                if (board) boardWindow.InsertImage(asset);
                else shell.InsertDesktopImage(asset);
                await Task.Delay(180);
                Check("insertion selects image " + board, controller.Tool == InkTool.Select && controller.SelectedElements.Count == 1
                    && controller.Selection.Count == 0 && store.CurrentImages.Count == 1);
                var center = controller.SelectionFrame.Center(controller.ViewTransform);
                controller.ClearSelection();
                Drag(controller, center, center);
                Check("image click selects object " + board, controller.SelectedElements.Count == 1);
                var imageViews = board ? Descendants(boardWindow.Content).OfType<BoardImageLayer>().Single() : null;
                if (board) await Task.Delay(160);
                controller.ClearSelection();
                var native = await NativeClick(center, boardWindow, board, shell);
                checks.Add(new { check = "native image selection " + board, available = native,
                    passed = native ? (bool?)(controller.SelectedElements.Count == 1) : null });
                if (!native) Drag(controller, center, center);
                var corners = controller.SelectionFrame.Corners(controller.ViewTransform);
                controller.ClearSelection();
                Drag(controller, corners[0] - new Vector2(30), corners[2] + new Vector2(30));
                Check("image marquee selects object " + board, controller.SelectedElements.Count == 1);
                controller.History.Clear();
                controller.OnPointerDown(Pointer(center));
                controller.OnPointerMove(Pointer(center + new Vector2(60, 40)));
                Check("image drag shows moving frame " + board, Vector2.Distance(controller.SelectionFrame.Center(controller.ViewTransform), center + new Vector2(60, 40)) < .01f);
                controller.OnPointerUp(Pointer(center + new Vector2(60, 40)));
                controller.Undo();
                Check("image move undo restores frame " + board, Vector2.Distance(controller.SelectionFrame.Center(controller.ViewTransform), center) < .01f);
                var toolbar = board ? Descendants(boardWindow.Content).OfType<SelectionToolbar>().Single()
                    : shell.OpenWindows.OfType<SelectionToolbarWindow>().Single().Toolbar;
                toolbar.Refresh();
                var properties = Descendants(toolbar).OfType<TouchIconButton>().Single(button => button.Label == "属性");
                Check("image properties enabled " + board, properties.IsEnabled);
                Invoke(properties);
                var opacity = Descendants(toolbar).OfType<ToggleButton>().Single(button => AutomationProperties.GetName(button) == "50%");
                opacity.IsChecked = true;
                Check("opacity menu edits image " + board, asset.Opacity == .5f);
                controller.Undo(); toolbar.Refresh();
                Check("opacity undo refreshes menu " + board, asset.Opacity == 1
                    && Descendants(toolbar).OfType<ToggleButton>().Single(button => AutomationProperties.GetName(button) == "100%").IsChecked == true);
                float width = controller.SelectionBounds.Width;
                Invoke(Descendants(toolbar).OfType<TouchIconButton>().Single(button => button.Label == "加宽"));
                Check("property controls resize image " + board, controller.SelectionBounds.Width > width * 1.09f);
                controller.Undo(); toolbar.Refresh();
                await Task.Delay(160);
                var root = board ? (FrameworkElement)boardWindow.Content : (FrameworkElement)shell.OpenWindows.OfType<SelectionToolbarWindow>().Single().Content;
                Check("properties fit selection toolbar " + board, Descendants(toolbar).OfType<TouchIconButton>().Where(button => button.ActualWidth > 0).All(button =>
                {
                    var bounds = button.TransformToVisual(root).TransformBounds(new Windows.Foundation.Rect(0, 0, button.ActualWidth, button.ActualHeight));
                    return bounds.Left >= -1 && bounds.Top >= -1 && bounds.Right <= root.ActualWidth + 1 && bounds.Bottom <= root.ActualHeight + 1;
                }));
                await BoardWindowRegression.SaveVisual(toolbar, Path.Combine(directory, $"image-properties-{board}.png")).WaitAsync(TimeSpan.FromSeconds(8));
                toolbar.ClosePanels();
                var frame = controller.SelectionFrame;
                var handle = frame.HandlePosition(SelectionHandle.BottomRight, controller.ViewTransform);
                Drag(controller, handle, handle + new Vector2(60, 40));
                Check("image corner resizes object " + board, controller.SelectionBounds.Width > width);
                controller.Undo();
                var rotate = controller.SelectionFrame.HandlePosition(SelectionHandle.Rotate, controller.ViewTransform);
                var rotated = Vector2.Transform(rotate, Matrix3x2.CreateRotation(.4f, center));
                Drag(controller, rotate, rotated);
                Check("image rotation follows handle " + board, MathF.Abs(controller.SelectionRotationDegrees) > 15);
                controller.Undo();
                controller.CloneMode = true;
                controller.OnPointerDown(Pointer(center));
                controller.OnPointerMove(Pointer(center + new Vector2(80, 50)));
                if (board)
                {
                    await Task.Delay(100);
                    Check("board clone uses shared decoded image and renders both objects", services.Board.CurrentImages.Count == 2
                        && Descendants(boardWindow.Content).OfType<BoardDrawingLayers>().Single().CachedImageCount == 1);
                }
                if (!board) await CompareOverlay("image clone preview preserves pixels");
                controller.OnPointerUp(Pointer(center + new Vector2(80, 50)));
                Check("image clone preserves source " + board, store.CurrentImages.Count == 2
                    && ReferenceEquals(((BoardSelectionElement)controller.SelectedElements.Single()).Image, asset));
                controller.Undo(); controller.CloneMode = false;
                string path = Path.Combine(directory, $"images-{board}.icink");
                store.SavePage(path);
                var restored = new BoardService(); restored.LoadPage(path);
                Check("image page saves and reloads " + board, restored.CurrentImages.Count == 1
                    && restored.CurrentImages[0].Data.SequenceEqual(asset.Data));
                if (board)
                {
                    await boardWindow.ExportSelectionImageAsync(Path.Combine(directory, "board-image-export.png"));
                    shell.ToggleBoard();
                }
                else await CompareOverlay("desktop image renders with correct view and dpi");
                if (!board)
                {
                    shell.CloneDesktopSelectionToBoard();
                    Check("desktop image transfers to board", services.Board.CurrentImages.Count == 1);
                    shell.ToggleBoard();
                    services.Board.ClearCurrentPage();
                }

                async Task CompareOverlay(string name)
                {
                    var presenter = shell.Overlay.Presenter;
                    presenter.Scene.ShowSelectionOutline = false;
                    await presenter.Scene.Images.PrepareAsync(device, controller);
                    controller.NotifyElementsChanged();
                    var window = shell.Overlay.Window;
                    using var expected = new CanvasRenderTarget(device, window.Width, window.Height, 96);
                    using var scene = new InkSceneRenderer { ShowSelectionOutline = false };
                    await scene.Images.PrepareAsync(device, controller);
                    using (var draw = expected.CreateDrawingSession())
                    {
                        draw.Clear(Windows.UI.Color.FromArgb(1, 1, 1, 1));
                        scene.DrawCommitted(draw, controller, controller.ViewTransform * Matrix3x2.CreateScale(window.DpiScale));
                    }
                    var actual = new byte[window.Width * window.Height * 4];
                    Marshal.Copy(window.PixelBuffer, actual, 0, actual.Length);
                    var reference = expected.GetPixelBytes();
                    int differences = actual.Zip(reference, (a, b) => Math.Abs(a - b)).Count(delta => delta > 3);
                    Check(name, differences < actual.Length / 20000 && actual.Where((_, index) => index % 4 == 3).Any(alpha => alpha > 200));
                    await expected.SaveAsync(Path.Combine(directory, name.Replace(' ', '-') + ".png"), CanvasBitmapFileFormat.Png);
                }
            }
            services.OverlayInk.Clear(); services.OverlayInk.Undo();
            Check("desktop clear restores image with undo", services.ScreenImages.CurrentImages.Count == 1);
        }
        catch (Exception ex) { checks.Add(new { check = "image objects exception", passed = false, error = ex.ToString() }); }
        finally
        {
            if (cursorAvailable) SetCursorPos(originalCursor.X, originalCursor.Y);
            File.WriteAllText(Path.Combine(directory, "image-objects.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            foreach (var window in shell.OpenWindows.ToArray()) window.Close();
            Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }

    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static async Task<bool> NativeClick(Vector2 center, BoardWindow boardWindow, bool board, ShellController shell)
    {
        int x, y;
        IntPtr expected;
        if (board)
        {
            var surface = Descendants(boardWindow.Content).OfType<InkSurface>().Single();
            var rect = WindowChrome.GetScreenBounds(boardWindow, surface);
            x = rect.X + (int)Math.Round(center.X * surface.XamlRoot.RasterizationScale);
            y = rect.Y + (int)Math.Round(center.Y * surface.XamlRoot.RasterizationScale);
            expected = WindowChrome.GetHwnd(boardWindow);
        }
        else
        {
            x = shell.Overlay.Window.X + (int)Math.Round(center.X * shell.Overlay.Window.DpiScale);
            y = shell.Overlay.Window.Y + (int)Math.Round(center.Y * shell.Overlay.Window.DpiScale);
            expected = shell.Overlay.Window.Handle;
        }
        if (!SetCursorPos(x, y)) return false;
        await Task.Delay(80);
        if (GetAncestor(WindowFromPoint(new NativeMethods.POINT { X = x, Y = y }), 2) != expected) return false;
        MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(30);
        MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(120);
        return true;
    }
    private static OverlayPointer Pointer(Vector2 point) => new(817, point.X, point.Y, .5f, 0, 0, OverlayPointerKind.Mouse, 1);
    private static void Drag(InkController controller, Vector2 from, Vector2 to)
    {
        controller.OnPointerDown(Pointer(from)); controller.OnPointerMove(Pointer(to)); controller.OnPointerUp(Pointer(to));
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
