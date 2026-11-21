using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.App.Views;
using InkCanvas.Features.Board;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class BoardPageRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        using var services = new AppServices(Path.Combine(directory, "settings"));
        services.Settings.Startup.IsAutoUpdate = false;
        services.Settings.PowerPoint.PowerPointSupport = false;
        services.Settings.Automation.MinimumAutomationStrokeNumber = int.MaxValue;
        var board = services.Board;
        board.Background = BoardBackground.Grid; board.GridSpacing = 56;
        for (int i = 1; i < 1000; i++) board.NextPage();
        board.Background = BoardBackground.Black;
        board.GoTo(1); board.Background = BoardBackground.Lined; board.GridSpacing = 24;
        board.GoTo(0);
        using var shell = new ShellController(services);
        shell.Start(); shell.StartInClickThrough();
        try
        {
            shell.ToggleBoard();
            await Task.Delay(300);
            var window = shell.OpenWindows.OfType<BoardWindow>().Single();
            var root = (FrameworkElement)window.Content;
            foreach (int index in new[] { 0, 1, 999 })
            {
                board.GoTo(index);
                await Task.Delay(120);
                string expected = $"{index + 1} / 1000";
                var indicators = Descendants(root).OfType<TextBlock>().Where(text => text.Text == expected).ToArray();
                bool fit = indicators.Length == 2 && indicators.All(text =>
                {
                    var host = (Viewbox)text.Parent;
                    var bounds = text.TransformToVisual(host).TransformBounds(new Windows.Foundation.Rect(0, 0, text.ActualWidth, text.ActualHeight));
                    return bounds.Left >= -1 && bounds.Right <= host.ActualWidth + 1
                        && bounds.Top >= -1 && bounds.Bottom <= host.ActualHeight + 1;
                });
                checks.Add(new { check = "four-digit page indicators fit", page = index + 1, passed = fit });
                var paper = index == 0 ? BoardBackground.Grid : index == 1 ? BoardBackground.Lined : BoardBackground.Black;
                checks.Add(new { check = "navigation binds the requested paper and controller", page = index + 1,
                    passed = board.Background == paper && ReferenceEquals(window.CurrentController, board.Pages[index]) });
                bool captured = await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, $"page-{index + 1}.png"));
                checks.Add(new { check = "live page visual captured", page = index + 1, passed = captured });
            }
            var add = Descendants(root).OfType<Button>().First(button => Descendants(button).OfType<TextBlock>().Any(text => text.Text == "加页"));
            ((IInvokeProvider)new ButtonAutomationPeer(add).GetPattern(PatternInterface.Invoke)).Invoke();
            await Task.Delay(100);
            checks.Add(new { check = "normal add-page button works beyond 999 pages and inherits paper",
                passed = board.PageCount == 1001 && board.CurrentIndex == 1000 && board.Background == BoardBackground.Black });
            board.Background = BoardBackground.White;
            string path = Path.Combine(directory, "pages.icink");
            await board.SaveBoardAsync(path);
            await board.LoadBoardAsync(path);
            await Task.Delay(100);
            bool restored = board.PageCount == 1001 && board.Background == BoardBackground.Grid && board.GridSpacing == 56;
            board.GoTo(999); restored &= board.Background == BoardBackground.Black;
            board.GoTo(1000); restored &= board.Background == BoardBackground.White;
            checks.Add(new { check = "1001-page board reopens with page-local paper in the live window", passed = restored });
        }
        catch (Exception ex) { checks.Add(new { check = "page regression exception", passed = false, error = ex.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "board-pages.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            shell.Dispose(); Application.Current.Exit();
        }
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject parent)
    {
        yield return parent;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(parent, i))) yield return child;
    }
}
