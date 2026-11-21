using System.Numerics;
using System.Runtime.InteropServices;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Selection;
using InkCanvas.Ink;
using InkCanvas.Platform.Interop;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

/// <summary>Opt-in desktop input check: exercise XAML routing and pointer capture, beyond controller maths.</summary>
internal static class SelectionWindowRegression
{
    public static async Task<bool?> Run(List<object> checks, string outputDirectory)
    {
        using var services = new AppServices();
        services.Settings.Automation.IsAutoSaveStrokesAtClear = false;
        var window = new BoardWindow(services);
        NativeMethods.GetCursorPos(out var originalCursor);
        try
        {
            window.Activate();
            await Task.Delay(350);
            var controller = window.CurrentController;
            var source = new InkStroke(new StrokeAttributes { BaseWidth = 4, TaperKind = TaperKind.None });
            foreach (var p in new[] { new Vector2(100, 100), new Vector2(200, 100), new Vector2(200, 160), new Vector2(100, 160), new Vector2(100, 100) })
                source.AddPoint(p.X, p.Y);
            source.Complete(); controller.Document.Add(source);
            controller.ViewTransform = Matrix3x2.Identity;
            var root = (FrameworkElement)window.Content;
            var surface = Descendants(root).OfType<InkSurface>().Single();
            bool boardUiWorks = await BoardWindowRegression.Run(window, services, checks, outputDirectory);
            window.ShowEraserPalette(); await Task.Delay(80);
            var eraserMenu = VisualTreeHelper.GetOpenPopupsForXamlRoot(root.XamlRoot).SelectMany(p => Descendants(p.Child)).OfType<EraserPalette>().Single();
            var lassoButton = Descendants(eraserMenu).OfType<TouchIconButton>().Single(button => button.Label == "套索擦");
            ((IInvokeProvider)new ButtonAutomationPeer(lassoButton).GetPattern(PatternInterface.Invoke)).Invoke();
            bool lassoButtonWorks = controller.Tool == InkTool.Eraser && controller.EraserMode == InkEraserMode.Lasso
                && surface.IsHitTestVisible;
            checks.Add(new { check = "board lasso eraser button activates canvas input", passed = lassoButtonWorks });
            // This also covers switching from the separate floating toolbar or a shortcut.
            controller.Tool = InkTool.Select;
            bool modeSynced = !surface.IsHitTestVisible;
            if (!await Drag(window, surface, new Vector2(70, 70), new Vector2(230, 190)))
            {
                checks.Add(new { check = "board XAML selection with native mouse input", available = false, modeSynced });
                return modeSynced && lassoButtonWorks && boardUiWorks ? null : false;
            }
            bool marquee = controller.Selection.Count == 1 && ReferenceEquals(controller.Selection[0], source);
            var cloneButton = Descendants(root).OfType<Button>()
                .First(b => Descendants(b).OfType<TextBlock>().Any(t => t.Text == "克隆"));
            ((IInvokeProvider)new ButtonAutomationPeer(cloneButton).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(40);
            bool cloneToggled = controller.CloneMode;
            bool cloneInput = await Drag(window, surface, new Vector2(150, 130), new Vector2(310, 260));
            bool cloned = cloneInput && controller.Document.Count == 2 && source.Points[0].Position == new Vector2(100, 100);
            controller.Undo();
            bool cloneUndo = controller.Document.Count == 1;
            controller.CloneMode = false;
            var frame = controller.SelectionFrame;
            var from = frame.HandlePosition(SelectionHandle.Rotate, controller.ViewTransform);
            var to = Vector2.Transform(from, Matrix3x2.CreateRotation(MathF.PI / 6, frame.Center(controller.ViewTransform)));
            bool rotationInput = await Drag(window, surface, from, to);
            bool rotated = rotationInput && MathF.Abs(controller.SelectionRotationDegrees - 30) < 1;
            controller.Tool = InkTool.Pen;
            bool penRestored = surface.IsHitTestVisible && !controller.HasSelection;
            bool passed = boardUiWorks && lassoButtonWorks && modeSynced && marquee && cloneToggled && cloned && cloneUndo && rotated && penRestored;
            checks.Add(new { check = "board XAML selection with native mouse input", available = true,
                modeSynced, marquee, cloneToggled, cloned, cloneUndo, rotated, penRestored, passed });
            return passed;
        }
        finally
        {
            window.Close();
            SetCursorPos(originalCursor.X, originalCursor.Y);
        }
    }

    private static async Task<bool> Drag(Window window, FrameworkElement surface, Vector2 from, Vector2 to)
    {
        var rect = WindowChrome.GetScreenBounds(window, surface);
        double dpi = surface.XamlRoot.RasterizationScale;
        void Move(Vector2 point) => SetCursorPos(rect.X + (int)Math.Round(point.X * dpi), rect.Y + (int)Math.Round(point.Y * dpi));
        Move(from);
        await Task.Delay(50);
        if (!NativeMethods.GetCursorPos(out var cursor)
            || GetAncestor(WindowFromPoint(cursor), 2) != WindowChrome.GetHwnd(window)) return false;
        MouseEvent(2, 0, 0, 0, UIntPtr.Zero);
        try
        {
            await Task.Delay(40);
            for (int i = 1; i <= 6; i++)
            {
                Move(Vector2.Lerp(from, to, i / 6f));
                await Task.Delay(30);
            }
        }
        finally { MouseEvent(4, 0, 0, 0, UIntPtr.Zero); }
        await Task.Delay(100);
        return true;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }

    [DllImport("user32.dll")] private static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativeMethods.POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll", EntryPoint = "mouse_event")] private static extern void MouseEvent(uint flags, uint dx, uint dy, uint data, UIntPtr extra);
}
