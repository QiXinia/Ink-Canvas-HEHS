using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Lifecycle;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Settings;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using InkCanvas.Ink.Rendering;
using InkCanvas.Platform.System;
using InkCanvas.Platform.Windows;
using Microsoft.Graphics.Canvas;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Storage.Streams;

namespace InkCanvas.App.Diagnostics;

internal static class MemoryRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var measurements = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        using var shell = new ShellController(services);
        shell.Start();
        shell.StartInClickThrough();
        try
        {
            Check("unopened selection and auxiliary windows are not created", shell.OpenWindows.Count() == 1);
            var device = CanvasDevice.GetSharedDevice();
            byte[] image = await CreateImage(device);
            var ink = services.OverlayInk;
            ink.RecognizeShapes = false;
            shell.ToggleClickThrough();
            var pointer = new OverlayPointer(9821, 40, 100, .5f, 1, 1, OverlayPointerKind.Pen, 100);
            ink.OnPointerDown(pointer);
            for (int i = 1; i <= 50; i++) ink.OnPointerMove(pointer with { X = 40 + i * 7, Y = 100 + MathF.Sin(i * .1f) * 20, TimeMs = (uint)(100 + i * 8) });
            ink.OnPointerUp(pointer with { X = 390, Y = 80, TimeMs = 600 });
            services.ScreenImages.AddImage(new BoardImageAsset { Data = image, Left = 100, Top = 180, Width = 400, Height = 300 });
            await shell.Overlay.Presenter.Scene.Images.PrepareAsync(device, ink);
            ink.NotifyElementsChanged();
            var stroke = ink.Document.Strokes.Single();
            var points = stroke.Points.ToArray();
            int undo = ink.History.UndoCount;
            var view = ink.ViewTransform;
            Sample("active annotation");
            long warm = OwnedBytes();
            Check("active resources are retained", !shell.Overlay.TrimIdleResources(force: true) && OwnedBytes() == warm);

            shell.Overlay.Hide();
            Check("quick hide retains warm images and DIB", !shell.Overlay.TrimIdleResources()
                && shell.Overlay.Window.PixelBufferBytes > 0 && shell.Overlay.Presenter.Scene.Images.CachedBitmapCount == 1);
            Check("hidden annotation caches can be reclaimed", shell.Overlay.TrimIdleResources(force: true) && OwnedBytes() == 0);
            Sample("hidden annotation resources released");
            var screen = ScreenService.Virtual;
            shell.Overlay.SetDisplayBounds(new(screen.X - 100, screen.Y, screen.Width + 100, screen.Height));
            shell.Overlay.SetDisplayBounds(screen);
            Check("hidden viewport resize retains released DIB state", shell.Overlay.Window.PixelBufferBytes == 0);
            var timer = Stopwatch.StartNew();
            shell.Overlay.Show();
            double firstInkMs = timer.Elapsed.TotalMilliseconds;
            await shell.Overlay.Presenter.Scene.Images.PrepareAsync(device, ink);
            ink.NotifyElementsChanged();
            measurements.Add(new { phase = "restore annotation", firstInkMilliseconds = firstInkMs,
                includingImageMilliseconds = timer.Elapsed.TotalMilliseconds, releasedOwnedBytes = warm });
            Check("restoring hidden annotation preserves strokes history and view", stroke.Points.SequenceEqual(points)
                && ink.History.UndoCount == undo && ink.ViewTransform == view && shell.Overlay.Window.PixelBufferBytes > 0);
            Sample("annotation restored");
            shell.Overlay.SetInteractionMode(true, false);
            shell.Overlay.TrimIdleResources(force: true);
            Check("mouse mode also releases the presented DIB", shell.Overlay.Window.PixelBufferBytes == 0);
            shell.Overlay.SetInteractionMode(false, true);
            Check("drawing mode recreates the DIB synchronously", shell.Overlay.Window.PixelBufferBytes > 0
                && shell.Overlay.Presenter.Scene.Strokes.CachedGeometryCount > 0);

            ink.Tool = InkTool.Select;
            ink.SelectAll();
            await Task.Delay(100);
            Check("first selection creates its toolbar on demand", shell.OpenWindows.OfType<SelectionToolbarWindow>().Single().IsShown);
            shell.OpenWindows.OfType<SelectionToolbarWindow>().Single().Toolbar.ToggleCollapsed();
            ink.ClearSelection();
            await Task.Delay(70);
            Check("selection toolbar hides after clearing selection", !shell.OpenWindows.OfType<SelectionToolbarWindow>().Single().IsShown);
            await CheckImages();
            await CheckLazyShapes();
            await CheckWindowLifetime();
            shell.Overlay.SetInteractionMode(true, false);
            await Task.Delay(16_000);
            Check("unused selection toolbar is closed after its grace period", !shell.OpenWindows.OfType<SelectionToolbarWindow>().Any());
            Check("normal idle maintenance releases hidden annotation buffers", OwnedBytes() == 0);
            shell.Overlay.SetInteractionMode(false, true);
            ink.SelectAll();
            await Task.Delay(100);
            Check("recreated selection toolbar retains collapse preference", shell.OpenWindows.OfType<SelectionToolbarWindow>().Single().Toolbar.IsCollapsed);
        }
        catch (Exception exception) { checks.Add(new { check = "memory regression exception", passed = false, error = exception.ToString() }); }
        finally
        {
            foreach (var window in shell.OpenWindows.ToArray()) window.Close();
            File.WriteAllText(Path.Combine(directory, "memory.json"), JsonSerializer.Serialize(new { checks, measurements },
                new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }

        long OwnedBytes() => shell.Overlay.Window.PixelBufferBytes + shell.Overlay.Presenter.ManagedBufferBytes
            + shell.Overlay.Presenter.RenderTargetBytes + shell.Overlay.Presenter.Scene.Images.CachedBitmapBytes;
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
        void Sample(string phase)
        {
            using var process = Process.GetCurrentProcess();
            measurements.Add(new { phase, ownedRenderBytes = OwnedBytes(), privateBytes = process.PrivateMemorySize64,
                workingSetBytes = process.WorkingSet64, managedBytes = GC.GetTotalMemory(false),
                nativePixelBytes = shell.Overlay.Window.PixelBufferBytes, bitmapBytes = shell.Overlay.Presenter.Scene.Images.CachedBitmapBytes });
        }

        async Task CheckImages()
        {
            using var controller = new InkController();
            var board = new BoardService(controller);
            byte[] png = await CreateImage(CanvasDevice.GetSharedDevice());
            for (int i = 0; i < 24; i++) board.AddImage(new BoardImageAsset { Data = png.ToArray(),
                Left = i * 1800, Width = 600, Height = 400 });
            using var scene = new InkSceneRenderer();
            using var target = new CanvasRenderTarget(CanvasDevice.GetSharedDevice(), 800, 600, 96);
            using var cache = new CommittedInkLayer();
            using (var draw = target.CreateDrawingSession()) cache.Draw(draw, scene, controller, 800, 600);
            using var firstOnly = new InkController { GetSelectableElements = () => board.GetSelectionElements().Take(1) };
            await scene.Images.PrepareAsync(CanvasDevice.GetSharedDevice(), firstOnly);
            Check("offscreen images do not decode on initial draw", scene.Images.CachedBitmapCount == 1);
            await scene.Images.PrepareAsync(CanvasDevice.GetSharedDevice(), controller);
            long before = scene.Images.CachedBitmapBytes;
            scene.Images.TrimOutsideViewport(controller, Matrix3x2.Identity, new(0, 0, 800, 600));
            Check("offscreen bitmap cache remains bounded", scene.Images.CachedBitmapCount <= 12
                && scene.Images.CachedBitmapBytes <= 64 * 1024 * 1024);
            measurements.Add(new { phase = "offscreen images", beforeBytes = before, afterBytes = scene.Images.CachedBitmapBytes });
            scene.Images.Clear();
            var pending = scene.Images.PrepareAsync(CanvasDevice.GetSharedDevice(), firstOnly);
            scene.Images.Clear();
            await pending;
            Check("a cleared pending decode cannot repopulate released cache", scene.Images.CachedBitmapCount == 0);
            await scene.Images.PrepareAsync(CanvasDevice.GetSharedDevice(), firstOnly);
            Check("released images decode again on demand", scene.Images.CachedBitmapCount == 1);
        }

        async Task CheckLazyShapes()
        {
            var shapes = new ShapePreview(services.OverlayInk, 1);
            var window = new Window { Content = shapes, Title = "Lazy shape categories" };
            WindowChrome.MakeBorderless(window);
            WindowChrome.ShowWithoutActivation(window);
            try
            {
                await Task.Delay(80);
                int created = Descendants(shapes).OfType<Grid>().Count(grid => grid.Tag is ShapeMenuItem);
                Check("unopened shape categories do not create icon trees", created > 0 && created < shapes.ShapeCount);
                for (int i = 0; i < ShapeMenuCatalog.Categories.Length; i++) shapes.SelectCategory(i);
                Check("lazy categories expose every shape", Descendants(shapes).OfType<Grid>().Count(grid => grid.Tag is ShapeMenuItem) == shapes.ShapeCount);
            }
            finally { window.Close(); }
        }

        async Task CheckWindowLifetime()
        {
            int released = 0, restored = 0;
            var window = new Window { Content = new Grid(), Title = "Idle window resources" };
            WindowChrome.MakeStandard(window, 400, 300);
            using var lifetime = new WindowIdleResources(window, () => released++, () => restored++, TimeSpan.FromMilliseconds(150));
            WindowChrome.ShowWithoutActivation(window);
            try
            {
                await Task.Delay(50);
                window.AppWindow.Hide();
                WindowChrome.ShowWithoutActivation(window);
                await Task.Delay(200);
                Check("a quick return cancels delayed release", released == 0 && !lifetime.IsReleased);
                window.AppWindow.Hide();
                await Task.Delay(250);
                Check("hidden window releases after its grace period", released == 1 && lifetime.IsReleased);
                int previous = restored;
                WindowChrome.ShowWithoutActivation(window);
                await Task.Delay(70);
                Check("restoring a window resumes its resources once", restored == previous + 1 && !lifetime.IsReleased && !lifetime.ReleaseNow());
                var presenter = (Microsoft.UI.Windowing.OverlappedPresenter)window.AppWindow.Presenter;
                presenter.Minimize();
                await Task.Delay(250);
                Check("minimized window also releases idle resources", released == 2 && lifetime.IsReleased && !lifetime.IsActive);
                presenter.Restore();
                await Task.Delay(100);
                Check("restoring minimized window resumes its resources", !lifetime.IsReleased && lifetime.IsActive);
            }
            finally { window.Close(); }
        }
    }

    private static async Task<byte[]> CreateImage(CanvasDevice device)
    {
        using var image = new CanvasRenderTarget(device, 1024, 1024, 96);
        using (var draw = image.CreateDrawingSession()) draw.Clear(Microsoft.UI.Colors.CornflowerBlue);
        using var stream = new InMemoryRandomAccessStream();
        await image.SaveAsync(stream, CanvasBitmapFileFormat.Png);
        stream.Seek(0);
        var data = new byte[checked((int)stream.Size)];
        using var reader = new DataReader(stream);
        await reader.LoadAsync((uint)data.Length);
        reader.ReadBytes(data);
        return data;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
