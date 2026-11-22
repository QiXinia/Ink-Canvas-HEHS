using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Core.Diagnostics;
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
            // The shell performs its initial DPI placement on Loaded/Activated. Read the baseline
            // only after that settles, otherwise the restored target is the pre-initial spot
            // (measured probe: desktop=37,91 vs actual restore target 684,832).
            await Task.Delay(600);
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
                await Wait(() => services.PresentationActive && services.Ppt.Snapshot.IsSlideShowRunning && services.Ppt.Snapshot.Slide?.Index == 1,
                    stage, DescribeState);
                await Task.Delay(100);
                var snapshot = services.Ppt.Snapshot;
                Check("show detected", snapshot.IsSlideShowRunning && snapshot.WindowHandle != 0 && snapshot.IsWindowedShow == (showType == 4),
                    new { showType, snapshot.State, snapshot.IsWindowedShow, window = snapshot.WindowHandle.ToInt64() });
                // A windowed reading view can extend under the taskbar, but FloatingBarWindow clamps the bar
// to the desktop work area on purpose. Measure the same clamped rectangle so the check tests
// "bottom-centre of the usable area" instead of "inside PowerPoint's full client rect".
                var area = showType == 4
                    ? Intersect(WindowChrome.GetClientBounds(snapshot.WindowHandle),
                        ScreenService.FromWindow(snapshot.WindowHandle).WorkArea)
                    : ScreenService.FromWindow(snapshot.WindowHandle).Bounds;
                var pos = WindowChrome.GetClientPosition(bar);
                var client = bar.AppWindow.ClientSize;
                Check("toolbar docked at bottom centre", Math.Abs(pos.X + client.Width / 2d - area.X - area.Width / 2d) <= 2
                    && pos.Y + client.Height <= area.Y + area.Height && area.Y + area.Height - pos.Y - client.Height < 25,
                    new { showType, x = pos.X, y = pos.Y, width = client.Width, height = client.Height, area, bar = bar.DescribePlacement() });
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
                await Wait(() => services.Ppt.Snapshot.Slide?.Index == 2 && ink.Document.Count == (secondSlideHasInk ? 1 : 0),
                    "external GotoSlide(2)", DescribeState);
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
                await Wait(() => ink.Document.Count == 1200, "PptPrevious back to page 1", DescribeState);
                ink.Document.Changed -= Observe;
                Check("page ink replacement is atomic", counts.SequenceEqual(new[] { 1200 })
                    && shell.Overlay.Presenter.PresentedFrames == framesBefore + 1,
                    new { showType, counts, frames = shell.Overlay.Presenter.PresentedFrames - framesBefore, milliseconds = latency.Elapsed.TotalMilliseconds });
                shell.PptNext();
                // 6000ms: debounce retry (~180ms) plus COM latency. The probe at 15000ms reported
                // page 1/ink 1200 with no navigation at all, which is what QueuePptNavigationRetry fixes.
                await Wait(() => services.Ppt.Snapshot.Slide?.Index == 2 && ink.Document.Count == 1,
                    "PptNext back to page 2", DescribeState, timeoutMs: 6000);
                Check("returning to page restores its ink", ink.Document.Count == 1,
                    new { showType, ink = ink.Document.Count, state = DescribeState() });
                bar.SetFolded(true);
                await Task.Delay(30);
                bar.SetFolded(false);
                Check("folded presentation toolbar reopens", bar.AppWindow.IsVisible && bar.FoldHandles.Count == 0);
                ((dynamic)show).View.Exit(); show = null;
                await Wait(() => !services.PresentationActive, "show exit", DescribeState);
                await Task.Delay(80);
                Check("exit restores desktop toolbar position", WindowChrome.GetClientPosition(bar).Equals(desktop),
                    new { showType, desktop = new { x = desktop.X, y = desktop.Y }, restored = new { x = WindowChrome.GetClientPosition(bar).X, y = WindowChrome.GetClientPosition(bar).Y }, bar = bar.DescribePlacement() });
                await Task.Delay(400);
            }
        }
        catch (Exception ex)
        {
            Environment.ExitCode = 1;
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
        string DescribeState()
        {
            var snapshot = services.Ppt.Snapshot;
            string pptPage;
            try { pptPage = show is null ? "none" : ((int)((dynamic)show).View.Slide.SlideIndex).ToString(); }
            catch (Exception ex) { pptPage = "unavailable(" + ex.GetType().Name + ")"; }
            long lastNav = shell.PptNavigationLastExitMs;
            string navAge = lastNav == long.MinValue ? "never" : (Environment.TickCount64 - lastNav) + "ms-ago";
            var floatingBar = shell.OpenWindows.OfType<FloatingBarWindow>().FirstOrDefault();
            string barPlacement = floatingBar is null ? "bar=none" : floatingBar.DescribePlacement();
            return $"[state={snapshot.State} attached={snapshot.IsAttached} running={snapshot.IsSlideShowRunning}" +
                $" index={snapshot.Slide?.Index.ToString() ?? "-"} slideId={snapshot.Slide?.SlideId.ToString() ?? "-"}" +
                $" position={snapshot.ShowPosition} showState={snapshot.ShowState?.ToString() ?? "-"}" +
                $" windowed={snapshot.IsWindowedShow} hwnd=0x{snapshot.WindowHandle.ToInt64():X}" +
                $" pptPage={pptPage} presentationActive={services.PresentationActive}" +
                $" ink={services.OverlayInk.Document.Count} source={shell.PptNavigationSource}" +
                $" lastNav={navAge} events={services.Ppt.EventsConnected}" +
                $" lastFailure={services.Ppt.LastFailure?.Operation}:{services.Ppt.LastFailure?.Message} {barPlacement}]";
        }
        void Check(string name, bool passed, object detail = null)
        {
            if (!passed) Environment.ExitCode = 1;
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

    private static ScreenRect Intersect(ScreenRect a, ScreenRect b)
    {
        int left = Math.Max(a.X, b.X), top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.X + a.Width, b.X + b.Width);
        int bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return right > left && bottom > top ? new ScreenRect(left, top, right - left, bottom - top) : b;
    }
    /// <summary>
    /// Polls until <paramref name="condition"/> holds. On timeout the actual snapshot, ink count,
    /// live PowerPoint page and a short trace are included so a failure names the stuck state
    /// instead of only the stage.
    /// </summary>
    private static async Task Wait(Func<bool> condition, string stage = "state", Func<string> describe = null,
        int timeoutMs = 5000)
    {
        var watch = Stopwatch.StartNew();
        var trace = new List<string>();
        long nextSampleAtMs = 0;
        while (!condition())
        {
            long elapsed = watch.ElapsedMilliseconds;
            if (describe is not null && elapsed >= nextSampleAtMs && trace.Count < 24)
            {
                nextSampleAtMs = elapsed + 200;
                trace.Add(elapsed + "ms " + describe());
            }
            if (elapsed > timeoutMs)
            {
                string actual = describe is null ? string.Empty : " actual=" + describe();
                string samples = trace.Count == 0 ? string.Empty : " trace=" + string.Join(" | ", trace);
                FileLog.WriteLogToFile($"[PPT] wait '{stage}' timed out after {elapsed}ms.{actual}{samples}",
                    LogType.Error);
                throw new TimeoutException($"PPT state did not settle at '{stage}' after {elapsed}ms.{actual}{samples}");
            }
            await Task.Delay(5);
        }
        if (watch.ElapsedMilliseconds > 1000)
            FileLog.WriteLogToFile($"[PPT] wait '{stage}' settled after {watch.ElapsedMilliseconds}ms (slow)",
                LogType.Info);
    }
}
