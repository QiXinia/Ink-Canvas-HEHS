using System.Text.Json;
using InkCanvas.App.Services;
using InkCanvas.Controls.Controls;
using InkCanvas.Core.Ink;
using InkCanvas.Core.Shapes;
using InkCanvas.Ink;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace InkCanvas.App.Diagnostics;

internal static class SelectionStateRegression
{
    public static async Task Run(string directory)
    {
        Directory.CreateDirectory(directory);
        var checks = new List<object>();
        var window = new Window { Title = "选中态检查" };
        var root = new StackPanel { Spacing = 16, Padding = new Thickness(20) };
        window.Content = root;
        WindowChrome.MakeStandard(window, 820, 720);
        WindowFrame.Attach(window, root);
        window.Activate();
        using var controller = new InkController();
        try
        {
            foreach (int theme in new[] { 1, 2, 1 })
            {
                root.Children.Clear();
                WindowChrome.ApplyTheme(window, theme);
                root.Background = new SolidColorBrush(theme == 1 ? Microsoft.UI.Colors.White : Windows.UI.Color.FromArgb(255, 32, 32, 32));
                var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Width = 480 };
                var icon = new TouchIconButton { Glyph = Glyphs.Pen, Label = "墨迹", Width = 64, Height = 56 };
                var text = new TouchIconButton { Content = "2 人", Width = 64, Height = 56 };
                var composite = new TouchIconButton { Width = 100, Height = 56, Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children = { new FontIcon { Glyph = Glyphs.OpenStrokes, FontFamily = IconFontStyle.Font }, new TextBlock { Text = "打开" } },
                } };
                var module = new TouchIconButton { Glyph = Glyphs.Pen, Width = 56, Height = 56,
                    Style = (Style)Application.Current.Resources["IcModuleIconButtonStyle"] };
                var disabled = new TouchIconButton { Glyph = Glyphs.Pen, Width = 56, Height = 56, IsEnabled = false };
                var buttons = new[] { icon, text, composite, module, disabled };
                foreach (var button in buttons) { button.IsSelected = true; tools.Children.Add(button); }
                var scaled = new Viewbox { Child = tools, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left };
                root.Children.Add(scaled);
                var styles = new PenStylePicker { SelectedIndex = 2 };
                styles.Update(theme);
                root.Children.Add(styles);
                controller.EraserMode = InkEraserMode.WholeStroke;
                root.Children.Add(new EraserPalette(controller, 1, theme));
                controller.SelectShape(ShapeKind.Rectangle, false);
                var shapes = new ShapePreview(controller, theme);
                root.Children.Add(shapes);
                await Task.Delay(250);
                Check("selected tool retains keyboard focus visuals", icon.UseSystemFocusVisuals
                    && icon.Focus(FocusState.Keyboard) && icon.FocusState == FocusState.Keyboard, new { theme });
                var originalSurface = ((SolidColorBrush)Descendants(icon).OfType<SelectionIndicator>().Single()
                    .Children.OfType<Border>().First().Background).Color;
                WindowChrome.ApplyTheme(window, theme == 1 ? 2 : 1);
                await Task.Delay(80);
                var changedSurface = ((SolidColorBrush)Descendants(icon).OfType<SelectionIndicator>().Single()
                    .Children.OfType<Border>().First().Background).Color;
                Check("theme switch updates existing selected controls", originalSurface != changedSurface
                    && buttons.All(button => Descendants(button).OfType<SelectionIndicator>().Single().IsSelected), new { theme });
                WindowChrome.ApplyTheme(window, theme);
                await Task.Delay(80);
                foreach (double scale in new[] { .5, 1, 1.5 })
                {
                    scaled.Width = 480 * scale;
                    root.UpdateLayout();
                    foreach (var button in buttons)
                    {
                        foreach (var state in button.IsEnabled ? new[] { "Normal", "PointerOver", "Pressed" } : new[] { "Disabled" })
                        {
                            VisualStateManager.GoToState(button, state, false);
                            var selection = Descendants(button).OfType<SelectionIndicator>().Single();
                            var borders = selection.Children.OfType<Border>().ToArray();
                            var fill = ((SolidColorBrush)borders[0].Background).Color;
                            var marker = borders[1];
                            Check("tool selection survives pointer states and custom content",
                                selection.IsSelected && marker.ActualWidth > 0 && marker.ActualHeight == 3
                                && fill.R == fill.G && fill.G == fill.B && fill.A > 0
                                && AutomationProperties.GetItemStatus(button) == "已选中"
                                && selection.ActualWidth <= button.ActualWidth && selection.ActualHeight <= button.ActualHeight,
                                new { theme, scale, state, custom = button == text || button == composite, fill = fill.ToString() });
                        }
                    }
                    Check("selected eraser mode has one indicator", Descendants(root).OfType<EraserPalette>()
                        .SelectMany(Descendants).OfType<SelectionIndicator>().Count(item => item.IsSelected) == 1, new { theme, scale });
                    Check("selected shape has one indicator", Descendants(shapes).OfType<SelectionIndicator>().Count(item => item.IsSelected) == 1,
                        new { theme, scale });
                    await BoardWindowRegression.SaveVisual(root, Path.Combine(directory, $"selection-{theme}-{scale}.png"));
                }
                foreach (var button in buttons) button.IsSelected = false;
                Check("clearing selection hides indicators", buttons.All(button =>
                    !Descendants(button).OfType<SelectionIndicator>().Single().IsSelected
                    && AutomationProperties.GetItemStatus(button) == ""), new { theme });
                text.Content = "3 人";
                text.IsSelected = true;
                root.UpdateLayout();
                Check("replacing button content preserves selection", Equals(text.Content, "3 人")
                    && Descendants(text).OfType<SelectionIndicator>().Single().IsSelected, new { theme });
            }
        }
        catch (Exception ex) { Check("selection regression exception", false, ex.ToString()); }
        finally
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "selection-states.json"),
                JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
            window.Close();
            Application.Current.Exit();
        }
        void Check(string check, bool passed, object detail) => checks.Add(new { check, passed, detail });
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }
}
