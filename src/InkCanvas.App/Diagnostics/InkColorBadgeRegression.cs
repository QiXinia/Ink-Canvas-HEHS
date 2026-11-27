using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Features.Board;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class InkColorBadgeRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.Appearance.IsEnableDisPlayFloatBarText = true;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        services.Settings.Archive.SaveOnExit = false;
        using var shell = new ShellController(services);
        shell.Start();
        shell.StartInClickThrough();
        try
        {
            var bar = shell.OpenWindows.OfType<FloatingBarWindow>().Single();
            shell.ToggleClickThrough();
            await Task.Delay(200);
            foreach (int theme in new[] { 1, 2 })
            foreach (var color in new[] { InkColor.Black, InkColor.White, InkColor.Red })
            {
                services.Settings.Appearance.Theme = theme;
                services.Settings.Canvas.PenColor = services.Settings.Canvas.HighlighterColor = color.PackedArgb;
                services.ApplySettings();
                WindowChrome.ApplyTheme(bar, theme);
                await Task.Delay(40);
                CheckBadge(bar.Bar, color, top: false, $"active tool color settings {theme} {color.PackedArgb}");
                foreach (var tool in new[] { InkTool.Pen, InkTool.Highlighter, InkTool.Shape })
                {
                    services.OverlayInk.Tool = tool;
                    await Task.Delay(40);
                    CheckBadge(bar.Bar, color, top: false, $"annotation {theme} {color.PackedArgb} {tool}");
                    if (color == InkColor.Red)
                        await BoardWindowRegression.SaveVisual(bar.Bar, Path.Combine(directory, $"annotation-{theme}-{tool}.png"));
                }
                await BoardWindowRegression.SaveVisual(bar.Bar, Path.Combine(directory, $"annotation-{theme}-{color.PackedArgb}.png"));
            }
            shell.OpenPenPalette(false);
            await Task.Delay(120);
            var palette = shell.OpenWindows.OfType<PenPaletteWindow>().Single();
            Invoke(Descendants(palette.Content).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "蓝色"));
            await Task.Delay(80);
            CheckBadge(bar.Bar, services.OverlayInk.CurrentAttributes.Color, top: false, "annotation palette color change");
            services.Settings.Canvas.HighlighterColor = InkColor.Red.WithAlpha(20).PackedArgb;
            services.ApplySettings();
            services.OverlayInk.Tool = InkTool.Highlighter;
            await Task.Delay(40);
            CheckBadge(bar.Bar, InkColor.Red, top: false, "translucent highlighter retains a visible color badge");
            shell.ToggleClickThrough();
            Check("mouse mode hides color badges", VisibleBadges(bar.Bar).Length == 0);
            shell.ToggleBoard();
            await Task.Delay(160);
            var board = shell.OpenWindows.OfType<BoardWindow>().Single();
            foreach (var background in new[] { BoardBackground.White, BoardBackground.Black })
            foreach (double scale in new[] { 50d, 100d, 150d })
            {
                services.Board.Background = background;
                services.Settings.Appearance.BlackboardScale = scale;
                services.Settings.Canvas.PenColor = InkColor.Red.PackedArgb;
                services.Settings.Canvas.HighlighterColor = InkColor.Opaque(234, 179, 8).PackedArgb;
                services.ApplySettings();
                foreach (var tool in new[] { InkTool.Pen, InkTool.Highlighter, InkTool.Shape })
                {
                    board.CurrentController.Tool = tool;
                    await Task.Delay(40);
                    CheckBadge(board.Content, board.CurrentController.CurrentAttributes.Color, top: true, $"board {background} {scale} {tool}");
                    if (scale == 100)
                        await BoardWindowRegression.SaveVisual(board.DockSurfaces[1], Path.Combine(directory, $"board-{background}-{tool}.png"));
                }
                await BoardWindowRegression.SaveVisual(board.DockSurfaces[1], Path.Combine(directory, $"board-{background}-{scale}.png"));
            }
            board.CurrentController.Tool = InkTool.Pen;
            board.ShowPenPalette();
            await Task.Delay(120);
            var boardPalette = board.ToolPanels.SelectMany(panel => Descendants(panel.Content)).OfType<BoardPenPalette>().Single();
            var penColors = Descendants(boardPalette).OfType<InkColorPalette>().First();
            Invoke(Descendants(penColors).OfType<Button>().Single(button => AutomationProperties.GetName(button) == "蓝色"));
            await Task.Delay(80);
            CheckBadge(board.Content, board.CurrentController.CurrentAttributes.Color, top: true, "board palette color change");
            services.Board.NextPage();
            services.Board.GoTo(0);
            await Task.Delay(80);
            CheckBadge(board.Content, board.CurrentController.CurrentAttributes.Color, top: true, "page switch updates color badge");
            shell.ToggleBoard();
            await Task.Delay(200);
            shell.ToggleBoard();
            await Task.Delay(200);
            board = shell.OpenWindows.OfType<BoardWindow>().Single();
            CheckBadge(board.Content, board.CurrentController.CurrentAttributes.Color, top: true, "restoring board updates color badge");
            board.CurrentController.Tool = InkTool.Eraser;
            Check("eraser hides board color badges", VisibleBadges(board.Content).Length == 0);
        }
        catch (Exception error) { checks.Add(new { check = "ink color badge exception", passed = false, detail = error.ToString() }); }
        finally
        {
            foreach (var board in shell.OpenWindows.OfType<BoardWindow>().ToArray()) board.Close();
            await File.WriteAllTextAsync(Path.Combine(directory, "ink-color-badges.json"),
                JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose();
            Application.Current.Exit();
        }

        void Check(string check, bool passed) => checks.Add(new { check, passed });
        void CheckBadge(DependencyObject root, InkColor color, bool top, string context)
        {
            var badges = VisibleBadges(root);
            bool passed = badges.Length == 1 && badges[0].Color == color.WithAlpha(255)
                && badges[0].HorizontalAlignment == HorizontalAlignment.Right
                && badges[0].VerticalAlignment == (top ? VerticalAlignment.Top : VerticalAlignment.Bottom)
                && !badges[0].IsHitTestVisible && badges[0].ActualWidth == 10 && badges[0].ActualHeight == 10;
            checks.Add(new { check = context, passed, expected = color.PackedArgb,
                badges = badges.Select(badge => new { color = badge.Color.PackedArgb, badge.ActualWidth, badge.ActualHeight }) });
        }
    }

    private static InkColorBadge[] VisibleBadges(DependencyObject root) => Descendants(root).OfType<InkColorBadge>()
        .Where(badge => badge.Visibility == Visibility.Visible).ToArray();
    private static void Invoke(Button button) => ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
