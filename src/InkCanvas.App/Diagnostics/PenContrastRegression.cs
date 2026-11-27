using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Theming;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace InkCanvas.App.Diagnostics;

internal static class PenContrastRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var window = new Window { Title = "笔锋对比度检查" };
        var styles = new PenStylePicker();
        var nib = new SegmentedSelector(new[] { "圆形", "斜椭圆" });
        var root = new StackPanel { Width = 440, Spacing = 20, Padding = new Thickness(20), Children = { styles, nib } };
        window.Content = root;
        WindowChrome.MakeStandard(window, 500, 300);
        WindowFrame.Attach(window, root);
        window.Activate();
        try
        {
            await Task.Delay(250);
            var selectors = Descendants(styles).OfType<SegmentedSelector>().Append(nib).ToArray();
            foreach (int theme in new[] { 1, 2 })
            {
                styles.Update(theme);
                WindowChrome.ApplyTheme(window, theme);
                root.Background = new SolidColorBrush(theme == 1 ? Microsoft.UI.Colors.White : Color.FromArgb(255, 31, 31, 31));
                await Task.Delay(80);
                for (int index = 0; index < 8; index++)
                {
                    styles.SelectedIndex = index;
                    nib.SelectedIndex = index % 2;
                    root.UpdateLayout();
                    foreach (var selector in selectors)
                    foreach (var state in new[] { "Checked", "CheckedPointerOver", "CheckedPressed" })
                    {
                        var button = Descendants(selector).OfType<ToggleButton>().Single(item => item.IsChecked == true);
                        VisualStateManager.GoToState(button, state, false);
                        var presenter = Descendants(button).OfType<ContentPresenter>().First(item => Equals(item.Content, button.Content));
                        var background = ((SolidColorBrush)presenter.Background).Color;
                        var foreground = ((SolidColorBrush)presenter.Foreground).Color;
                        double ratio = ColorContrast.Ratio(ToInk(background), ToInk(foreground));
                        var labels = Descendants(button.Content as DependencyObject).OfType<TextBlock>();
                        var icons = Descendants(button.Content as DependencyObject).OfType<PenStyleIcon>();
                        bool inherited = labels.All(label => ((SolidColorBrush)label.Foreground).Color == foreground)
                            && icons.All(icon => ((SolidColorBrush)icon.Foreground).Color == foreground
                                && Descendants(icon).OfType<Microsoft.UI.Xaml.Shapes.Path>().Any()
                                && Descendants(icon).OfType<Microsoft.UI.Xaml.Shapes.Path>().All(path => path.Fill is SolidColorBrush brush && brush.Color == foreground));
                        checks.Add(new { check = "selected ink control contrast", theme, index, state,
                            styles = selector != nib, ratio, foreground = foreground.ToString(), background = background.ToString(),
                            passed = ratio >= 4.5 && inherited });
                    }
                }
                await BoardWindowRegression.SaveVisual((FrameworkElement)window.Content, Path.Combine(directory, $"pen-contrast-{theme}.png"));
            }
        }
        catch (Exception ex) { checks.Add(new { check = "pen contrast regression exception", passed = false, detail = ex.ToString() }); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "pen-contrast.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            window.Close(); Application.Current.Exit();
        }
        static InkColor ToInk(Color color) => new(color.A, color.R, color.G, color.B);
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        if (root is null) yield break;
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
