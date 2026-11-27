using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Reflection;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Core.Ink;
using InkCanvas.Controls.Controls;
using InkCanvas.Controls.Theming;
using InkCanvas.Features.Tools;
using InkCanvas.Platform.Interop;
using InkCanvas.Platform.System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

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
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        services.Settings.Automation.AutoSavedStrokesLocation = Path.Combine(Path.GetFullPath(directory), "saved-ink");
        services.Settings.Automation.AutoSavedStrokesLocation = Path.Combine(Path.GetFullPath(directory), "saved-ink");
        using var shell = new ShellController(services);
        object application = null, presentation = null, show = null;
        var ownedCom = new List<object>();
        string stage = "setup";
        try
        {
            shell.Start(); shell.StartInClickThrough();
            for (int attempt = 0; attempt < 8; attempt++)
            {
                await Task.Delay(250);
                foreach (var notice in shell.OpenWindows.OfType<NoticeWindow>().ToArray()) notice.Close();
            }
            shell.Overlay.SetInteractionMode(clickThrough: true, inkVisible: true);
            await Task.Delay(300);
            foreach (var notice in shell.OpenWindows.OfType<NoticeWindow>().ToArray()) notice.Close();
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
                    catch (COMException ex) when (attempt < 8 && (ex.HResult is unchecked((int)0x80004005) or unchecked((int)0x80010001)))
                    { await Task.Delay(300); }
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
                shell.Overlay.SetInteractionMode(clickThrough: false, inkVisible: true);
                var area = showType == 4 ? WindowChrome.GetClientBounds(snapshot.WindowHandle)
                    : ScreenService.FromWindow(snapshot.WindowHandle).Bounds;
                shell.Overlay.SetInteractionMode(clickThrough: false, inkVisible: true);
                var pos = WindowChrome.GetClientPosition(bar);
                var client = bar.AppWindow.ClientSize;
                Check("toolbar docked at bottom centre", Math.Abs(pos.X + client.Width / 2d - area.X - area.Width / 2d) <= 2
                    && pos.Y + client.Height <= area.Y + area.Height && area.Y + area.Height - pos.Y - client.Height < 25,
                    new { showType, x = pos.X, y = pos.Y, width = client.Width, height = client.Height, area });
                if (Environment.GetCommandLineArgs().Contains("--pptcheck-navigation-only"))
                {
                    stage = "navigation layout " + showType;
                    Check("PPT mouse-mode hidden ink defaults off", !services.Settings.PowerPoint.HideStrokesAfterLeavingAnnotation);
                    var annotation = services.OverlayInk;
                    annotation.Document.ReplaceAll(new[] { Stroke(120, 120) });
                    shell.ToggleClickThrough();
                    Check("PPT mouse mode preserves ink visibility by default",
                        shell.Overlay.ClickThrough && shell.Overlay.Presenter.InkVisible && annotation.Document.Count == 1);
                    shell.ToggleClickThrough();
                    services.Settings.PowerPoint.HideStrokesAfterLeavingAnnotation = true;
                    shell.ToggleClickThrough();
                    Check("PPT optional hide suppresses ink without clearing it",
                        shell.Overlay.ClickThrough && !shell.Overlay.Presenter.InkVisible && annotation.Document.Count == 1);
                    shell.ToggleClickThrough();
                    services.Settings.PowerPoint.HideStrokesAfterLeavingAnnotation = false;
                    shell.ToggleClickThrough();
                    await CheckNavigation(showType, bar);
                    ((dynamic)show).View.Exit(); show = null;
                    await Wait(() => !services.PresentationActive);
                    await Task.Delay(120);
                    Check("exit restores desktop toolbar position", WindowChrome.GetClientPosition(bar).Equals(desktop),
                        new { showType, desktop, restored = WindowChrome.GetClientPosition(bar) });
                    continue;
                }
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
                await Task.Delay(200); // Respect the navigation debounce between distinct clicks.
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
        async Task CheckNavigation(int showType, FloatingBarWindow bar)
        {
            var navigationMap = (Dictionary<PptNavigationPlacement, PptNavigationWindow>)typeof(ShellController)
                .GetField("_pptNavigations", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shell);
            var navigation = navigationMap.GetValueOrDefault(PptNavigationPlacement.BottomLeft);
            if (navigation is null) throw new InvalidOperationException("PPT navigation window was not created.");
            var card = navigation.NavigationCard;
            var buttons = Descendants(card).OfType<Button>().ToArray();
            var reference = Descendants(bar.Bar).OfType<TouchIconButton>().First(button => button.IsEnabled && button.Visibility == Visibility.Visible);
            foreach (int theme in new[] { 1, 2 })
            foreach (int scale in new[] { 50, 100, 150, 200 })
            foreach (bool counter in new[] { false, true })
            {
                services.Settings.Appearance.Theme = theme;
                services.Settings.Appearance.FloatingBarScale = scale;
                services.Settings.PowerPoint.IsShowPPTNavigationBottom = counter;
                services.Settings.PowerPoint.IsShowPPTNavigationSides = counter;
                services.ApplySettings();
                foreach (var placement in Enum.GetValues<PptNavigationPlacement>())
                {
                    var placedNavigation = navigationMap.GetValueOrDefault(placement);
                    if (placedNavigation is null) continue;
                    await Task.Delay(60);
                    var nav = WindowChrome.GetClientBounds(WindowChrome.GetHwnd(placedNavigation));
                    var toolbar = WindowChrome.GetClientBounds(WindowChrome.GetHwnd(bar));
                    var bounds = bar.GetPlacementArea();
                    bool bottom = placement is PptNavigationPlacement.BottomLeft or PptNavigationPlacement.BottomRight;
                    bool overlap = nav.X < toolbar.X + toolbar.Width && nav.X + nav.Width > toolbar.X
                        && nav.Y < toolbar.Y + toolbar.Height && nav.Y + nav.Height > toolbar.Y;
                    double margin = placement is PptNavigationPlacement.SideLeft or PptNavigationPlacement.SideRight
                        ? ScreenService.FromWindow(WindowChrome.GetHwnd(placedNavigation)).DpiScale * 6 : 0;
                    Check("navigation bounds align with toolbar", !overlap && nav.X >= bounds.X && nav.Y >= bounds.Y
                        && nav.X + nav.Width <= bounds.X + bounds.Width && nav.Y + nav.Height <= bounds.Y + bounds.Height
                        && (!bottom || Math.Abs(nav.Y + nav.Height - toolbar.Y - toolbar.Height + margin) <= 2),
                        new { showType, theme, scale, counter, placement, nav, toolbar });
                }
                    var buttonBounds = WindowChrome.GetScreenBounds(navigation, buttons[0]);
                    Check("navigation keeps the original three-cell layout", buttons.Length == 3
                        && buttonBounds.Width > 0 && buttonBounds.Height > 0
                        && ((SolidColorBrush)((FontIcon)buttons[0].Content).Foreground).Color == PopupBrushes.FloatBarForeground(theme).Color,
                        new { showType, theme, scale, counter, navigation.Placement, buttonBounds });
                    Check("navigation uses toolbar translucent theme colors " + theme,
                        ((SolidColorBrush)card.Background).Color == PopupBrushes.FloatBarBackground(theme).Color
                        && ((SolidColorBrush)card.BorderBrush).Color == PopupBrushes.FloatBarBorder(theme).Color,
                        new { showType, theme, background = ((SolidColorBrush)card.Background).Color,
                            border = ((SolidColorBrush)card.BorderBrush).Color });
                    if (scale == 100 && counter)
                    {
                        await BoardWindowRegression.SaveVisual((FrameworkElement)navigation.Content,
                            Path.Combine(directory, $"navigation-card-{showType}-theme-{theme}.png"));
                    }
            }
            services.Settings.Appearance.FloatingBarScale = 100;
            services.Settings.Appearance.Theme = 1;
            services.ApplySettings();
            await Task.Delay(220);
            navigation.RecordInteraction();
            await Task.Delay(5100);
            Check("navigation window reaches 75 percent opacity after five idle seconds", navigation.TargetOpacity == .75);
            navigation.RecordInteraction();
            Check("navigation interaction fades back to full opacity", navigation.TargetOpacity == 1);
            await Task.Delay(300);
            Check("navigation window fade completes at normal opacity", Math.Abs(navigation.WindowOpacity - 1) < .01);
            services.Settings.Appearance.IsColorfulViewboxFloatingBar = true;
            services.Settings.Appearance.IsTransparentButtonBackground = false;
            services.ApplySettings();
            await Task.Delay(100);
            Check("navigation follows toolbar background options", card.Background is LinearGradientBrush);
            services.Settings.Appearance.IsColorfulViewboxFloatingBar = false;
            services.Settings.Appearance.IsTransparentButtonBackground = true;
            services.ApplySettings();
            Invoke(buttons.Single(button => AutomationProperties.GetName(button) == "下一页"));
            await Wait(() => services.Ppt.Snapshot.Slide?.Index == 2);
            Check("navigation next button changes actual PowerPoint page", services.Ppt.Snapshot.Slide?.Index == 2);
            await Task.Delay(220);
            Invoke(buttons.Single(button => AutomationProperties.GetName(button) == "上一页"));
            await Wait(() => services.Ppt.Snapshot.Slide?.Index == 1);
            Check("navigation previous button changes actual PowerPoint page", services.Ppt.Snapshot.Slide?.Index == 1);
        }
        void Check(string name, bool passed, object detail = null)
        {
            checks.Add(new { check = name, passed, detail });
            File.AppendAllText(Path.Combine(directory, "progress.log"), name + " " + passed + Environment.NewLine);
            File.WriteAllText(Path.Combine(directory, "ppt.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    private static void Invoke(Button button)
        => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();

    private static IEnumerable<DependencyObject> Descendants(DependencyObject element)
    {
        yield return element;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(element); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(element, index))) yield return child;
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
