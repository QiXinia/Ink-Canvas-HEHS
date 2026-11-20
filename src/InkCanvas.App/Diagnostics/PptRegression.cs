using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Core.Ink;
using InkCanvas.Platform.System;
using Microsoft.UI.Xaml;

namespace InkCanvas.App.Diagnostics;

internal static class PptRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var keeper = new Window { Content = new Microsoft.UI.Xaml.Controls.Grid() };
        keeper.Activate(); keeper.AppWindow.Hide();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.PowerPoint.IsNotifyHiddenPage = false;
        services.Settings.PowerPoint.IsNotifyPreviousPage = false;
        services.Settings.PowerPoint.IsNotifyAutoPlayPresentation = false;
        services.Settings.PowerPoint.IsAutoSaveScreenShotInPowerPoint = false;
        services.Settings.PowerPoint.IsAutoSaveStrokesInPowerPoint = false;
        services.Settings.Automation.IsAutoFoldInPPTSlideShow = false;
        using var shell = new ShellController(services);
        object application = null, presentation = null, show = null;
        var ownedCom = new List<object>();
        string stage = "setup";
        try
        {
            shell.Start(); shell.StartInClickThrough();
            shell.Overlay.SetInteractionMode(clickThrough: true, inkVisible: true);
            await Task.Delay(300);
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            var work = ScreenService.FromWindow(WindowChrome.GetHwnd(bar)).WorkArea;
            var size = bar.AppWindow.ClientSize;
            WindowChrome.PlaceClient(bar, work.X + 37, work.Y + 91, size.Width, size.Height);
            await Task.Delay(60);
            var desktop = WindowChrome.GetClientPosition(bar);
            application = Activator.CreateInstance(Type.GetTypeFromProgID("PowerPoint.Application", throwOnError: true));
            dynamic app = application;
            dynamic shows = Own((object)app.SlideShowWindows);
            if ((int)shows.Count != 0) throw new InvalidOperationException("A slideshow is already running. This diagnostic preserves existing presentations.");
            dynamic presentations = Own((object)app.Presentations);
            presentation = Own((object)presentations.Add(-1));
            dynamic document = presentation;
            dynamic slides = Own((object)document.Slides);
            for (int index = 1; index <= 2; index++) Own((object)slides.Add(index, 12));
            dynamic settings = Own((object)document.SlideShowSettings);
            settings.AdvanceMode = 1;
            document.SaveAs(Path.Combine(Path.GetFullPath(directory), "slideshow-test.pptx"), 24);
            bool secondSlideHasInk = false;
            foreach (int showType in Environment.GetCommandLineArgs().Contains("--pptcheck-reading-only") ? new[] { 4 } : new[] { 1, 4 })
            {
                services.Settings.PowerPoint.PowerPointSupport = false;
                services.Ppt.Detach();
                stage = "show type " + showType;
                settings.ShowType = showType;
                dynamic documentWindows = Own((object)document.Windows);
                dynamic documentWindow = Own((object)documentWindows[1]);
                documentWindow.Activate();
                await Task.Delay(500);
                stage = "run show " + showType;
                for (int attempt = 0; show is null; attempt++)
                {
                    try { show = Own((object)settings.Run()); }
                    catch (COMException ex) when (attempt < 2 && ex.HResult is unchecked((int)0x80004005) or unchecked((int)0x80010001))
                    { await Task.Delay(200); }
                }
                await Task.Delay(200);
                services.Ppt.TryAttach();
                services.Settings.PowerPoint.PowerPointSupport = true;
                Check("native PowerPoint events connected", services.Ppt.EventsConnected);
                stage = "view " + showType;
                dynamic view = Own((object)((dynamic)show).View);
                stage = "goto first " + showType;
                view.GotoSlide(1);
                stage = "wait first " + showType;
                await Wait(() => services.PresentationActive && services.Ppt.Snapshot.IsSlideShowRunning && services.Ppt.Snapshot.Slide?.Index == 1);
                await Task.Delay(100);
                var snapshot = services.Ppt.Snapshot;
                Check("show detected", snapshot.IsSlideShowRunning && snapshot.WindowHandle != 0 && snapshot.IsWindowedShow == (showType == 4),
                    new { showType, snapshot.State, snapshot.IsWindowedShow, window = snapshot.WindowHandle.ToInt64() });
                var area = showType == 4 ? WindowChrome.GetClientBounds(snapshot.WindowHandle)
                    : ScreenService.FromWindow(snapshot.WindowHandle).Bounds;
                var pos = WindowChrome.GetClientPosition(bar);
                var client = bar.AppWindow.ClientSize;
                Check("toolbar docked at bottom centre", Math.Abs(pos.X + client.Width / 2d - area.X - area.Width / 2d) <= 2
                    && pos.Y + client.Height <= area.Y + area.Height && area.Y + area.Height - pos.Y - client.Height < 25,
                    new { showType, x = pos.X, y = pos.Y, width = client.Width, height = client.Height, area });
                var ink = services.OverlayInk;
                shell.Overlay.SetInteractionMode(clickThrough: true, inkVisible: true);
                ink.Document.ReplaceAll(Enumerable.Range(0, 1200).Select(index => Stroke(40 + index % 40 * 12, 50 + index / 40 * 10)));
                ink.FinishDotAnimations();
                await Task.Delay(100);
                long stableFrames = shell.Overlay.Presenter.PresentedFrames;
                await Task.Delay(1100);
                Check("idle slideshow preserves ink and presented frame", ink.Document.Count == 1200
                    && stableFrames == shell.Overlay.Presenter.PresentedFrames, new { showType, stableFrames, after = shell.Overlay.Presenter.PresentedFrames, ink.Document.Count });
                var latency = Stopwatch.StartNew();
                view.GotoSlide(2);
                await Wait(() => services.Ppt.Snapshot.Slide?.Index == 2 && ink.Document.Count == (secondSlideHasInk ? 1 : 0));
                Check("external page navigation switches ink promptly", latency.ElapsedMilliseconds < 250,
                    new { showType, milliseconds = latency.Elapsed.TotalMilliseconds });
                ink.Document.ReplaceAll(new[] { Stroke(200, 200) });
                secondSlideHasInk = true;
                long framesBefore = shell.Overlay.Presenter.PresentedFrames;
                var counts = new List<int>();
                void Observe() => counts.Add(ink.Document.Count);
                ink.Document.Changed += Observe;
                latency.Restart();
                shell.PptPrevious();
                await Wait(() => ink.Document.Count == 1200);
                ink.Document.Changed -= Observe;
                Check("page ink replacement is atomic", counts.SequenceEqual(new[] { 1200 })
                    && shell.Overlay.Presenter.PresentedFrames == framesBefore + 1,
                    new { showType, counts, frames = shell.Overlay.Presenter.PresentedFrames - framesBefore, milliseconds = latency.Elapsed.TotalMilliseconds });
                shell.PptNext();
                await Wait(() => services.Ppt.Snapshot.Slide?.Index == 2 && ink.Document.Count == 1);
                Check("returning to page restores its ink", ink.Document.Count == 1);
                bar.SetFolded(true);
                await Task.Delay(30);
                bar.SetFolded(false);
                Check("folded presentation toolbar reopens", bar.AppWindow.IsVisible && bar.FoldHandles.Count == 0);
                ((dynamic)show).View.Exit(); show = null;
                await Wait(() => !services.PresentationActive);
                await Task.Delay(80);
                Check("exit restores desktop toolbar position", WindowChrome.GetClientPosition(bar).Equals(desktop),
                    new { showType, desktop, restored = WindowChrome.GetClientPosition(bar) });
                await Task.Delay(400);
            }
        }
        catch (Exception ex)
        {
            checks.Add(new { check = "PPT regression exception", passed = false, stage, error = ex.ToString() });
            File.WriteAllText(Path.Combine(directory, "ppt.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
        finally
        {
            try { if (show is not null) ((dynamic)show).View.Exit(); } catch { }
            services.Settings.PowerPoint.PowerPointSupport = false;
            try { if (presentation is not null) { ((dynamic)presentation).Saved = -1; ((dynamic)presentation).Close(); } } catch { }
            for (int i = ownedCom.Count - 1; i >= 0; i--) try { Marshal.ReleaseComObject(ownedCom[i]); } catch { }
            if (application is not null) Marshal.ReleaseComObject(application);
            await File.WriteAllTextAsync(Path.Combine(directory, "ppt.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose();
            foreach (var window in shell.OpenWindows.ToArray()) window.Close();
            keeper.Close(); Application.Current.Exit();
        }
        object Own(object value) { ownedCom.Add(value); return value; }
        void Check(string name, bool passed, object detail = null)
        {
            checks.Add(new { check = name, passed, detail });
            File.AppendAllText(Path.Combine(directory, "progress.log"), name + " " + passed + Environment.NewLine);
            File.WriteAllText(Path.Combine(directory, "ppt.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static InkStroke Stroke(float x, float y)
    {
        var stroke = new InkStroke(new StrokeAttributes { BaseWidth = 3 });
        stroke.AddPoint(x, y, .5f, 0); stroke.AddPoint(x + 8, y + 4, .5f, 20); stroke.Complete();
        return stroke;
    }
    private static async Task Wait(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            if (watch.ElapsedMilliseconds > 5000) throw new TimeoutException("PPT state did not settle.");
            await Task.Delay(5);
        }
    }
}
