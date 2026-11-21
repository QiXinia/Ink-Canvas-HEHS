using System.Runtime.InteropServices;
using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Ink;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class PopupRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        using var shell = new ShellController(services);
        var outside = new Window { Title = "Popup input acceptance", Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.White) } };
        WindowChrome.MakeStandard(outside, topmost: false);
        WindowChrome.PlaceClient(outside, 50, 60, 600, 400);
        outside.Activate();
        shell.Start(); shell.StartInClickThrough();
        bool nativePointer = NativeMethods.GetCursorPos(out var original);
        checks.Add(new { check = "native pointer input availability", available = nativePointer });
        try
        {
            await Task.Delay(300);
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            services.Settings.Canvas.EraserType = 3;
            services.ApplySettings();
            foreach (var tool in new[] { InkTool.Pen, InkTool.Highlighter, InkTool.Eraser, InkTool.Shape })
            {
                Invoke((Button)bar.Bar.GetToolButton(tool)); await Task.Delay(160);
                Check("first tool click selects without opening options " + tool, services.OverlayInk.Tool == tool
                    && !shell.OpenWindows.Any(window => window is PenPaletteWindow or ShapePickerWindow)
                    && bar.GetOpenPanel(BarPanelKind.Eraser) is null);
                Invoke((Button)bar.Bar.GetToolButton(tool)); await Task.Delay(160);
                Check("second tool click opens options " + tool, tool switch
                {
                    InkTool.Shape => shell.OpenWindows.OfType<ShapePickerWindow>().Any(),
                    InkTool.Eraser => bar.GetOpenPanel(BarPanelKind.Eraser) is not null,
                    _ => shell.OpenWindows.OfType<PenPaletteWindow>().Any(),
                });
                foreach (var window in shell.OpenWindows.Where(window => window is PenPaletteWindow or ShapePickerWindow).ToArray()) window.Close();
                bar.GetOpenPanel(BarPanelKind.Eraser)?.Close();
            }
            Check("reselecting eraser preserves last lasso choice", services.OverlayInk.EraserMode == InkEraserMode.Lasso);
            foreach (bool highlighter in new[] { false, true })
            {
                shell.OpenPenPalette(highlighter); await Task.Delay(200);
                var window = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
                var palette = Descendants(window.Content).OfType<PenPalette>().Single();
                palette.Header.IsPinned = false;
                bool closed = false; window.Closed += (_, _) => closed = true;
                Check("pen panel has compact title actions " + highlighter, Descendants(palette.Header).OfType<Button>().Any()
                    && palette.Header.ActualHeight >= 40 && palette.Header.ActualHeight <= 44);
                Check("pen panel preserves no-activation " + highlighter, NativeMethods.HasExStyle(WindowChrome.GetHwnd(window), NativeMethods.WS_EX_NOACTIVATE));
                await BoardWindowRegression.SaveVisual((FrameworkElement)window.Content, Path.Combine(directory, highlighter ? "highlighter.png" : "pen.png"));
                Click(100, 200); await Task.Delay(200);
                if (!nativePointer) window.DismissOnOverlayInput();
                Check("quick outside click dismisses pen panel " + highlighter, closed);
                if (!closed) window.Close();

                shell.OpenPenPalette(highlighter); await Task.Delay(150);
                window = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
                palette = Descendants(window.Content).OfType<PenPalette>().Single();
                palette.Header.IsPinned = false;
                closed = false; window.Closed += (_, _) => closed = true;
                var attachedPosition = window.AppWindow.Position;
                bool collapsed = bar.Bar.IsMainCollapsed;
                await Drag(bar, bar.Bar);
                Check("toolbar drag does not toggle collapse " + highlighter, bar.Bar.IsMainCollapsed == collapsed);
                Check("pen panel follows toolbar drag before detaching " + highlighter,
                    !closed && !window.IsDetached && window.AppWindow.Position != attachedPosition);
                await Drag(window, palette.Header);
                Check("dragging detaches and pins pen panel " + highlighter, window.IsDetached && palette.Header.IsPinned);
                var detachedPosition = window.AppWindow.Position;
                bar.Reposition(95, 100, recenter: false); await Task.Delay(100);
                Check("dragged pen panel does not follow toolbar " + highlighter, window.AppWindow.Position == detachedPosition);
                Invoke(Descendants(palette).OfType<Button>().First(button => AutomationProperties.GetName(button) == (highlighter ? "荧光笔黄色" : "红色")));
                var saved = services.Store.Load().Canvas;
                Check("tool color survives settings reload " + highlighter, (highlighter ? saved.HighlighterColor : saved.PenColor) == services.OverlayInk.CurrentAttributes.Color.PackedArgb);
                closed = false; window.Closed += (_, _) => closed = true;
                Click(100, 200); await Task.Delay(200);
                window.DismissOnOverlayInput();
                Check("pinned pen panel survives outside click and writing " + highlighter, !closed);
                palette.Header.IsPinned = false;
                Click(100, 200); await Task.Delay(200);
                if (!nativePointer) window.DismissOnOverlayInput();
                Check("unpin re-enables light dismiss " + highlighter, closed);
                if (!closed) window.Close();
            }

            foreach (var kind in new[] { BarPanelKind.More, BarPanelKind.Gesture, BarPanelKind.Eraser })
            {
                using var close = new WindowCloseGuard();
                var panel = new BarPanelWindow(bar, bar.Bar, kind); close.Window = panel;
                var anchor = bar.AppWindow.Position;
                panel.ShowAboveBar(anchor.X, anchor.Y, bar.AppWindow.Size.Width); await Task.Delay(180);
                var header = Descendants(panel.Content).OfType<PopupHeader>().Single();
                bool closed = false; panel.Closed += (_, _) => { closed = true; close.Window = null; };
                Check("toolbar panel uses shared compact header " + kind, header.ActualHeight >= 40);
                if (kind == BarPanelKind.Eraser)
                {
                    var combo = Descendants(panel.Content).OfType<ComboBox>().Single();
                    combo.IsDropDownOpen = true; await Task.Delay(180);
                    var item = VisualTreeHelper.GetOpenPopupsForXamlRoot(((FrameworkElement)panel.Content).XamlRoot)
                        .SelectMany(popup => Descendants(popup.Child)).OfType<ComboBoxItem>().Last();
                    var screen = WindowChrome.GetScreenBounds(panel, item);
                    var bounds = new Windows.Foundation.Rect(screen.X, screen.Y, screen.Width, screen.Height);
                    SetCursorPos((int)(bounds.X + bounds.Width / 2), (int)(bounds.Y + bounds.Height / 2));
                    var target = WindowFromPoint(new NativeMethods.POINT { X = (int)(bounds.X + bounds.Width / 2), Y = (int)(bounds.Y + bounds.Height / 2) });
                    var nativeClass = new System.Text.StringBuilder(256); GetClassName(target, nativeClass, nativeClass.Capacity);
                    Click((int)(bounds.X + bounds.Width / 2), (int)(bounds.Y + bounds.Height / 2));
                    await Task.Delay(180);
                    if (!nativePointer) { combo.SelectedIndex = combo.Items.Count - 1; combo.IsDropDownOpen = false; }
                    checks.Add(new { check = "combo selection keeps its unpinned parent panel open", passed = !closed && !combo.IsDropDownOpen,
                        closed, combo.IsDropDownOpen, combo.SelectedIndex, nativeClass = nativeClass.ToString(), bounds });
                    if (closed) continue;
                }
                var attachedPosition = panel.AppWindow.Position;
                await Drag(bar, bar.Bar);
                Check("toolbar panel follows toolbar drag before detaching " + kind,
                    !closed && !panel.IsDetached && panel.AppWindow.Position != attachedPosition);
                await Drag(panel, header);
                Check("toolbar panel detaches and pins " + kind, panel.IsDetached && header.IsPinned);
                var detachedPosition = panel.AppWindow.Position;
                panel.PlaceAboveBar(anchor.X + 60, anchor.Y, bar.AppWindow.Size.Width);
                Check("detached toolbar panel retains placement " + kind, panel.AppWindow.Position == detachedPosition);
                header.IsPinned = true; Click(100, 200); await Task.Delay(180);
                panel.DismissOnInput(); Check("pinned toolbar panel remains open " + kind, !closed);
                header.IsPinned = false; Click(100, 200); await Task.Delay(180);
                if (!nativePointer) panel.DismissOnInput();
                Check("toolbar panel dismisses after unpin " + kind, closed);
            }

            shell.OpenPenPalette(false); await Task.Delay(120);
            var detachedPen = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
            detachedPen.Detach();
            shell.OpenPenPalette(true); await Task.Delay(120);
            var detachedHighlighter = shell.OpenWindows.OfType<PenPaletteWindow>().Single(window => window.IsHighlighter);
            detachedHighlighter.Detach();
            Invoke((Button)bar.Bar.GetToolButton(InkTool.Eraser)); await Task.Delay(100);
            Check("detached pen and highlighter coexist across tool changes", shell.OpenWindows.OfType<PenPaletteWindow>().Count() == 2);
            var detachedPalette = Descendants(detachedPen.Content).OfType<PenPalette>().Single();
            Invoke(Descendants(detachedPalette).OfType<Button>().First(button => AutomationProperties.GetName(button) == "红色"));
            Check("editing detached pen activates its own tool", services.OverlayInk.Tool == InkTool.Pen
                && services.Store.Load().Canvas.PenColor == services.OverlayInk.CurrentAttributes.Color.PackedArgb);
            detachedPen.Close(); detachedHighlighter.Close();

            shell.OpenShapePicker(); await Task.Delay(150);
            var attachedShapes = shell.OpenWindows.OfType<ShapePickerWindow>().Single();
            bool attachedShapesClosed = false;
            attachedShapes.Closed += (_, _) => attachedShapesClosed = true;
            var attachedShapesPosition = attachedShapes.AppWindow.Position;
            await Drag(bar, bar.Bar);
            Check("shape panel follows toolbar drag before detaching", !attachedShapesClosed && !attachedShapes.IsDetached
                && attachedShapes.AppWindow.Position != attachedShapesPosition);
            if (!attachedShapesClosed) attachedShapes.Close();

            shell.ToggleBoard(); await Task.Delay(200);
            var board = shell.OpenWindows.OfType<BoardWindow>().Single();
            foreach (var tool in new[] { InkTool.Eraser, InkTool.Pen })
            {
                var anchor = board.GetToolAnchor(tool);
                var button = Descendants(board.Content).OfType<Button>().First(item => WindowChrome.GetScreenBounds(board, item).Equals(anchor));
                Invoke(button); await Task.Delay(120);
                Check("board first tool click selects without options " + tool, board.CurrentController.Tool == tool && !board.ToolPanels.Any());
                Invoke(button); await Task.Delay(120);
                Check("board second tool click opens options " + tool, board.ToolPanels.Count() == 1);
                foreach (var panel in board.ToolPanels.ToArray()) panel.Close();
            }
            shell.OpenShapePicker(); await Task.Delay(150);
            var shapes = shell.OpenWindows.OfType<ShapePickerWindow>().Single();
            await Drag(shapes, Descendants(shapes.Content).OfType<PopupHeader>().Single());
            Check("shape panel drag disables automatic hiding", shapes.IsDetached && !shapes.AutoHide);
            bool shapeClosed = false; shapes.Closed += (_, _) => shapeClosed = true;
            Click(150, 200); await Task.Delay(180);
            Check("pinned shapes survive clicks on owner board", !shapeClosed);
            shapes.AutoHide = true;
            Click(150, 200); await Task.Delay(180);
            if (!nativePointer) shapes.DismissOnOverlayInput();
            Check("quick owner-board click dismisses unpinned shapes", shapeClosed);
            if (!shapeClosed) shapes.Close();
            board.ShowPenPalette(); await Task.Delay(180);
            var popup = board.ToolPanels.Single();
            bool boardPanelClosed = false; popup.Closed += (_, _) => boardPanelClosed = true;
            var inkHeader = Descendants(popup.Content).OfType<PopupHeader>().Single();
            Check("board ink settings use shared compact title actions", inkHeader.ActualHeight >= 40);
            await Drag(popup, inkHeader);
            Check("board ink panel can detach and pin", popup.IsDetached && inkHeader.IsPinned);
            var controller = services.Board.Current;
            controller.Tool = InkTool.Pen;
            int before = controller.Document.Count;
            WritePoint(board, 160, 210, nativePointer); await Task.Delay(200);
            Check("pinned board ink panel stays open while first click writes", !boardPanelClosed && controller.Document.Count == before + 1);
            inkHeader.IsPinned = false;
            WritePoint(board, 170, 220, nativePointer); await Task.Delay(180);
            Check("unpinned board ink panel dismisses and first click writes", boardPanelClosed && controller.Document.Count == before + 2);
            board.ShowPenPalette(); await Task.Delay(150);
            popup = board.ToolPanels.Single();
            boardPanelClosed = false; popup.Closed += (_, _) => boardPanelClosed = true;
            inkHeader = Descendants(popup.Content).OfType<PopupHeader>().Single();
            Invoke(Descendants(inkHeader).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "关闭面板"));
            await Task.Delay(120);
            Check("board panel close action dismisses its window", boardPanelClosed);
            board.Close();
            using var restored = new AppServices(services.Store.Directory);
            Check("new application services restore saved pen options", restored.OverlayInk.CurrentAttributes.Color.PackedArgb == services.Settings.Canvas.PenColor);
            restored.OverlayInk.Tool = InkTool.Highlighter;
            Check("new application services restore saved highlighter options", restored.OverlayInk.CurrentAttributes.Color.PackedArgb == services.Settings.Canvas.HighlighterColor);
            Check("new application services restore lasso eraser", restored.OverlayInk.EraserMode == InkEraserMode.Lasso);
        }
        catch (Exception exception) { checks.Add(new { check = "popup regression exception", passed = false, error = exception.ToString() }); }
        finally
        {
            foreach (var window in shell.OpenWindows.ToArray()) window.Close();
            outside.Close(); SetCursorPos(original.X, original.Y);
            File.WriteAllText(Path.Combine(directory, "popups.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            Application.Current.Exit();
        }
        void Check(string name, bool passed) => checks.Add(new { check = name, passed });
    }
    private sealed class WindowCloseGuard : IDisposable
    {
        public Window Window { get; set; }
        public void Dispose() => Window?.Close();
    }
    private static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
        MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
    }
    private static async Task Drag(Window window, FrameworkElement header)
    {
        if (!NativeMethods.GetCursorPos(out _))
        {
            switch (window)
            {
                case PenPaletteWindow pen: pen.Detach(); break;
                case ShapePickerWindow shapes: shapes.Detach(); break;
                case BarPanelWindow panel: panel.Detach(); break;
                case ToolOptionsWindow options: options.Detach(); break;
            }
            var position = window.AppWindow.Position;
            window.AppWindow.Move(new Windows.Graphics.PointInt32(position.X - 96, position.Y - 64));
            WindowChrome.KeepVisible(window);
            await Task.Delay(100);
            return;
        }
        // The outside-click acceptance window can be foreground/topmost after the previous
        // case. Raise this target before injecting input so the drag reaches the intended HWND.
        WindowChrome.EnsureAbove(window);
        await Task.Delay(45);
        var bounds = WindowChrome.GetScreenBounds(window, header);
        int x = bounds.X + 20, y = bounds.Y + bounds.Height / 2;
        SetCursorPos(x, y);
        await Task.Delay(35); // Let WinUI route pointer entry to the newly positioned island.
        MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(60);
        for (int step = 1; step <= 8; step++) { SetCursorPos(x - step * 12, y - step * 8); await Task.Delay(25); }
        MouseEvent(4, 0, 0, 0, UIntPtr.Zero);
        await Task.Delay(160);
    }
    private static void WritePoint(BoardWindow board, int x, int y, bool nativePointer)
    {
        if (nativePointer) { Click(x, y); return; }
        board.OnCanvasInputBegan();
        var pointer = new InkCanvas.Platform.Windows.OverlayPointer(9774, x, y, .5f, 0, 0,
            InkCanvas.Platform.Windows.OverlayPointerKind.Mouse, 1);
        board.CurrentController.OnPointerDown(pointer);
        board.CurrentController.OnPointerUp(pointer);
    }
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int length);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
